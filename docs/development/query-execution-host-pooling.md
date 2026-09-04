# Out-of-process query host latency: findings and a pooling design

[← Back to Development Guide](../../DEVELOPMENT.md)

Code: [`src/DotnetEfCoreMcp.Server/Querying/OutOfProcessRoslynQueryExecutor.cs`](../../src/DotnetEfCoreMcp.Server/Querying/OutOfProcessRoslynQueryExecutor.cs) ·
[`src/DotnetEfCoreMcp.QueryHost/Program.cs`](../../src/DotnetEfCoreMcp.QueryHost/Program.cs) ·
[`src/DotnetEfCoreMcp.Server/Querying/RoslynQueryExecutor.cs`](../../src/DotnetEfCoreMcp.Server/Querying/RoslynQueryExecutor.cs)

Related: [Query execution](query-execution.md) · [Query execution alternatives](query-execution-alternatives.md) · [Roslyn-compiled `UserQuery`](roslyn-user-query.md)

## Summary

Every out-of-process `run_query` call (`QueryExecution:Mode=OutOfProcess`, or `Auto`, which
currently always resolves to out-of-process) costs **~1.6–2.1s** end to end on this dev machine.
That is a real latency tax for interactive/agentic use, where an LLM may call `run_query`
repeatedly in one session and would otherwise expect in-process-tool-call latency (5–100ms).

This investigation measured, with real phase-level timings, **where that time actually goes**,
and found it is **not** `dotnet exec`/runtime process-startup cost as originally hypothesized.
Raw process spin-up is only ~90–250ms. The dominant cost — roughly **1.0–1.4s of every single
call, >60% of total latency** — is a **fresh Roslyn `CSharpCompilation` of the generated user
query source**, compiled from a cold JIT state in a brand-new process every time.

A throwaway proof-of-concept confirmed that reusing an already-warm process (same JIT'd
Roslyn/EF Core code, same loaded reference assemblies) drops **per-query latency after the first
by ~15–20x** (from ~1.7–2.3s down to ~65–130ms), which is squarely in the interactive-latency
range the task is targeting. Given the size of this win, **a persistent/pooled host is worth
building**, but it must be designed carefully because it trades away part of the isolation
guarantee the out-of-process design exists for. This document lays out the evidence, the
tradeoffs, and a concrete design; it does not implement the pool (see
["Why this isn't implemented yet"](#why-this-isnt-implemented-yet)).

## 1. Where the time actually goes (measured)

Method: a throwaway instrumented harness (deleted after use; not part of this repo) drove the
real `OutOfProcessRoslynQueryExecutor` code path against the existing
[`tests/Fixtures/SampleApp`](../../tests/Fixtures/SampleApp) fixture (a small EF Core + SQLite
app), on a Release build, .NET SDK 10.0.400, Windows. `RoslynQueryExecutor.ExecuteAsync` and
`QueryHost/Program.cs` were temporarily instrumented with `Stopwatch`-based phase markers
(reverted afterwards; not part of this change).

**Baselines** (no query work at all):

| Measurement | Result |
|---|---|
| `dotnet --version` (pure CLI cold start) | ~85–98ms |
| `dotnet exec QueryHost.dll` with empty stdin (process spin-up + immediate failure, no query work) | ~90–250ms |

**Full current pipeline**, 10 sequential distinct `run_query` calls through the real
one-process-per-query path:

| Metric | Result |
|---|---|
| Per-call range | 1,634–2,107ms |
| Average per call | 1,876ms |
| Total for 10 calls | 18,763ms |

**Phase breakdown of a single representative call** (`elapsed=1852ms` total). The host-level phases
below run first and are cumulative from process start; the executor-internal phases run inside
`RoslynQueryExecutor.ExecuteAsync` (which starts once the host-level phases finish, at 107ms) and
are shown separately, cumulative from *that* start, to avoid mixing two different zero points in
one table:

*Host phases (cumulative from process start):*

| Phase | Cumulative | Phase cost |
|---|---|---|
| read stdin | 6ms | 6ms |
| deserialize request | 61ms | 55ms |
| load target assembly | 104ms | 43ms |
| resolve context type | 107ms | 3ms |

*Executor phases (cumulative from `RoslynQueryExecutor.ExecuteAsync` start, i.e. +107ms above)*:

| Phase | Cumulative | Phase cost |
|---|---|---|
| generate query source | 46ms | 46ms |
| **compile (Roslyn `CSharpCompilation`)** | **1,151ms** | **~1,100ms** |
| load compiled assembly into ALC | 1,163ms | 12ms |
| create `DbContext` | 1,240ms | 77ms |
| invoke `RunUserAuthoredQuery` | 1,514ms | 274ms |
| shape/materialize result | 1,686ms | 172ms |
| *(executor total)* | 1,798ms | — |

Overall wall-clock total for this call was measured as 1,852ms (`elapsed` above); the two tables'
own reference points sum to slightly more (107ms + 1,798ms ≈ 1,905ms) due to small overlap between
the host's stop-the-clock point and the executor's start-the-clock point in the temporary
instrumentation, not a real 53ms of missing/duplicated work — both are within normal
Stopwatch-boundary noise for numbers of this size.

**Conclusion:** process/IO overhead (spawn, stdin, deserialize, assembly load, type resolve) is
only ~100–210ms — a small fraction of the total. **Roslyn compilation of the dynamically
generated query source is the dominant cost, at ~1.0–1.4s per call**, because both (a) the
`Microsoft.CodeAnalysis.CSharp` compiler assemblies themselves must be JIT-compiled from scratch
in every fresh process (no cross-process JIT cache in .NET), and (b) real semantic compilation of
a C# file with metadata references to EF Core, the provider, and the target assembly is
inherently non-trivial work, made worse by doing it from a cold JIT/type-load state. DbContext
construction, query invocation, and result shaping add a further ~250–500ms, also partly
JIT-cost-inflated by running in a fresh process.

This directly overturns the task's initial hypothesis that most of the cost is `dotnet
exec`/runtime startup — that part is cheap. **The expensive part is the per-query compilation and
EF/JIT warm-up work that happens inside the freshly started process, not the act of starting the
process itself.**

## 2. Is process pooling/reuse viable? (proof of concept)

If the dominant cost is JIT/compiler warm-up rather than process creation, then reusing an
already-warm process across queries should amortize that cost after the first query. A second
throwaway PoC tested this directly: a loop-based host reads newline-delimited JSON requests from
stdin repeatedly instead of exiting after one, reusing the same process (and therefore the same
JIT'd Roslyn/EF Core code and loaded assemblies) across calls, using the exact same
`RoslynQueryExecutor`/`QueryCompiler`/`AssemblyLoaderService` pipeline as the real `QueryHost`.

Feeding it the same 10 representative, distinct queries used above, sequentially, over one
persistent process:

| Query | Round-trip |
|---|---|
| 1st (cold) | ~1,860–3,400ms — same order of magnitude as one-shot |
| 2nd–10th (warm) | ~63–133ms each, average ~83ms |

Total wall time (process start + all 10 queries + exit) was **~2.7s**, versus **~18.8s** for the
current one-process-per-query model running the same 10 queries — about **7x faster for the
sequence as a whole**, and **~15–20x faster per query** once the process is warm. The
warm-per-query cost (~80ms) is in the same range as the in-process tool-call latency (5–100ms)
cited as the target in the task description.

**Verdict: process pooling/reuse is a clearly viable and high-value mitigation.** The size of the
win (>90% latency reduction per query after the first) is too large to leave on the table for a
tool meant to support interactive/agentic use, provided the isolation tradeoffs below can be
adequately mitigated.

## 3. Isolation tradeoffs — why the out-of-process design exists, and what pooling risks

The out-of-process host exists specifically so that a crashing, hanging, or malicious/buggy
user-authored query cannot take down or corrupt the main MCP server process, and so each query
gets to run from clean state (see [Query execution alternatives](query-execution-alternatives.md)
for the original design rationale: `dotnet ef`/LINQPad-style host-under-target-runtime
isolation). Reusing a process across queries is in direct tension with "clean state per query."
Each risk below is weighed against the measured win.

| Risk | Detail | Mitigation |
|---|---|---|
| **Hung/malicious query blocks a pooled worker** | A query that spins forever or deadlocks would tie up a reusable process indefinitely instead of just failing and letting the next one-shot process start clean. This is strictly worse than today only if the pool has no per-query timeout — today there is *implicitly* a timeout equal to "the caller gives up and the whole process gets killed" (see `OutOfProcessRoslynQueryExecutor.KillIfRunning`, which already establishes kill-on-failure as an existing pattern to build on). | Enforce a hard per-query wall-clock timeout inside the pool manager (already have `CancellationToken` plumbed through `ExecuteAsync`). On timeout, `Kill(entireProcessTree: true)` the worker (exactly like today's cancellation path) and **remove it from the pool** rather than returning it — replace with a freshly spawned (cold) worker. Net effect: a hang costs one caller their timeout, exactly like today, and does not degrade other callers beyond losing one warm slot temporarily. |
| **State leakage between distinct queries in the same process** | The current pipeline already creates a **fresh `DbContext`** and loads compiled query code into a **fresh, collectible `CompiledQueryLoadContext`** per query (see `RoslynQueryExecutor.ExecuteAsync`), and disposes/unloads both after each call — this isolation is already per-query, not per-process, so it is preserved unchanged by pooling. What pooling *adds* exposure to is **process-wide static state**: JIT/type caches, `AssemblyLoaderService`'s in-memory cache of previously loaded target assemblies (`Load`/`LoadCore` — an `internal` cache keyed by path/name), and Roslyn's own internal caches (e.g. compilation reference/metadata caches). | Two safe boundaries: (1) key pool membership by **target assembly path + its last-write timestamp**, so a process only ever serves the target app it was warmed against, and a rebuilt target app cannot be served by a worker holding a stale copy; (2) recycle (retire and replace) a pooled worker after **N served queries or M minutes idle/alive**, bounding both memory growth and staleness exposure — this is a standard pattern for "warm worker pools" in other ecosystems (e.g. PHP-FPM `pm.max_requests`, ASP.NET Core's own worker recycling). |
| **Unbounded memory growth from many distinct query shapes over a long session** | Each distinct query text produces a distinct generated/compiled assembly loaded into the process (via a collectible ALC, which *can* be unloaded — but Roslyn's own compilation caches and JIT'd code for the compiler itself are not per-ALC and persist for the process's life). A long agentic session issuing hundreds of different queries against a persistently warm process could grow memory over time. | The recycle policy above (max requests per worker) directly bounds this — no worker lives long enough to accumulate unbounded state. Also cap **pool size** (e.g. 1–2 idle warm workers) rather than growing it unboundedly; a pool is for warm *readiness*, not concurrency scaling. |
| **Confusing cross-target-app reuse** | If the server is later extended to serve multiple target apps/connections in one session (the named multi-target assembly registry, [P2 #15](WORK-TRACKER.md), suggests this is already a direction), a pool must not hand a worker warmed for target app A a query meant for target app B — different EF Core/provider versions, different `runtimeconfig.json`. | Pool keying (above) already handles this: pools are keyed per target assembly identity, so cross-target reuse is structurally impossible, not just discouraged. |
| **Losing the "kill this exact process = fully contained" mental model** | Operationally, the current model's simplicity ("every query gets exactly one process, which then dies") is easy to reason about for security review. | Keep one-shot execution as the *default*/fallback path (already true via `QueryExecution:Mode`); ship pooling as an explicit opt-in mode (e.g. `QueryExecution:Mode=Pooled`) so the isolation-simple path remains available and is what ships by default until the pooled path has field experience. |

None of these risks are novel — they are the same class of tradeoffs every warm-worker-pool
design (PHP-FPM, Node cluster workers, serverless "provisioned concurrency," ASP.NET Core's app
domain recycling) already solves with the same two levers: **bounded worker lifetime (recycle
after N uses) and hard per-request timeouts with kill-and-replace**. Given the existing codebase
already has both a `CancellationToken`-driven kill path (`KillIfRunning`) and a
target-assembly-keyed loading abstraction (`AssemblyLoaderService`/`LoadedAssemblyHandle`) to
build on, none of the mitigations require new infrastructure — they compose with what already
exists.

**Conclusion: the isolation tradeoffs are real but well-understood and mitigable** with
(a) per-query timeout + kill-and-replace, (b) worker recycling after N queries, (c) pool keyed by
target-assembly identity, and (d) making pooling an explicit opt-in mode rather than the default.
Given the ~15–20x per-query and ~7x total-sequence latency win, this is worth pursuing.

## 4. Design: a bounded, recycling worker pool

### 4.1 Shape of the change

Add a new executor, `PooledOutOfProcessRoslynQueryExecutor`, alongside (not replacing)
`OutOfProcessRoslynQueryExecutor`, selected via a new `QueryExecutionMode.Pooled` value (in
addition to today's `InProcess`/`OutOfProcess`/`Auto`). `Auto` continues to resolve to
`OutOfProcess` (unchanged, safe default) until pooling has field experience; `Pooled` is
explicit opt-in via `QueryExecution:Mode=Pooled`.

### 4.2 Protocol change

`QueryHost/Program.cs` currently reads all of stdin once, handles exactly one request, writes one
response, and exits (see the "one-shot" top-level statements). A pooled worker needs a
**long-lived variant** that loops: read one newline-delimited JSON request line, execute it,
write one newline-delimited JSON response line, flush, and loop — until it receives a sentinel
"shutdown" request or its parent connection is closed. This was validated directly by the PoC's
persistent-host prototype, which reused the existing `RoslynQueryExecutor` unchanged in a
`while` loop over stdin lines.

Concretely:

- Extend `OutOfProcessQueryProtocol` with an explicit `shutdown` request variant (or a
  `RequestId == null` sentinel) so the pool manager can cleanly retire a worker.
- Keep the JSON payload shapes (`OutOfProcessQueryRequest`/`OutOfProcessQueryResponse`)
  unchanged — only the framing changes from "one request, EOF" to "newline-delimited requests
  until shutdown," so existing (de)serialization code is reused as-is.
- The one-shot `QueryHost/Program.cs` path is **kept** unmodified as the default entry point;
  the persistent loop is a **new command-line mode** (e.g. `QueryHost.dll --persistent`) so
  `OutOfProcessRoslynQueryExecutor`'s existing one-shot callers are entirely unaffected.
- **Self-terminating idle timeout (defense in depth)**: the persistent worker does not rely
  solely on the pool manager to retire it. It tracks its own last-activity timestamp and exits
  on its own (e.g. after 2× `QueryExecution:PoolIdleTimeoutSeconds`, so the pool manager's own
  idle recycling — §4.3 — is normally the one that fires first) if it receives no request in
  that window. This protects host OS resources even if the parent MCP server process crashes,
  is killed without a clean shutdown, or otherwise loses track of a worker it spawned — an
  orphaned pooled `dotnet` process must never be able to sit around indefinitely.

### 4.3 Pool manager (new type, e.g. `QueryHostPool` in `DotnetEfCoreMcp.Server/Querying`)

- **Keying**: one sub-pool per `(target assembly full path, target assembly last-write-time
  UTC)`. A rebuild of the target app naturally invalidates its sub-pool key, so a stale worker
  is never handed a query against a newer build. Because the key includes the *full absolute
  path*, this also does the right thing across git worktrees or multiple checkouts of the same
  repo — each worktree's target assembly resolves to a different path and therefore a distinct,
  non-shared sub-pool; there is no risk of a worker warmed for one worktree's build being handed
  a query meant for another's.
- **Sizing and a global cap**: bounded **per key** (e.g. 1 warm idle worker per key by default,
  configurable via `QueryExecution:PoolMaxWorkersPerTarget`, default 1–2) *and* bounded
  **globally across all keys** within one server process (e.g.
  `QueryExecution:PoolMaxTotalWorkers`, default e.g. 8). The per-key cap alone is not enough:
  a single long-running MCP server session that touches many distinct target apps (or many
  stale builds of the same app, each with a different last-write-time key) could otherwise
  accumulate an unbounded number of idle warm processes over time. This is a *latency-hiding*
  pool, not a concurrency-scaling pool; the goal is "the next query doesn't pay cold-start," not
  "serve N queries in parallel."
- **Backpressure when the global cap is reached**: a checkout that would exceed
  `PoolMaxTotalWorkers` does **not** fail the query. It falls back to spawning a plain
  non-pooled one-shot worker for that call (same behavior as today's `OutOfProcess` mode) and,
  if needed, evicts the least-recently-used idle worker from another key to make room for a
  *new* pooled worker the next time that key is used. Denying or queuing the caller's request
  outright would turn a latency optimization into a new failure mode, which defeats the point;
  degrading to today's baseline latency for the overflow case is always safe.
- **Multi-instance / multi-worktree deployments**: the pool lives entirely in-process within one
  MCP server instance and has no cross-process coordination. If multiple MCP server instances
  run concurrently against the same or different target apps — a realistic scenario for this
  project, since parallel agent sessions are commonly run from separate git worktrees, each
  starting its own MCP server — each instance maintains its **own independent pool** up to its
  own `PoolMaxTotalWorkers`. Total system-wide pooled-worker count is therefore
  `PoolMaxTotalWorkers × (number of concurrently running MCP server instances)`, not a
  system-wide bound. The default `PoolMaxTotalWorkers` should stay conservative (e.g. single
  digits) specifically because of this multiplicative effect; a true system-wide bound (e.g. a
  named OS semaphore or lock file shared across instances) is a possible future enhancement but
  is out of scope for the initial rollout — call this out explicitly wherever
  `PoolMaxTotalWorkers` is documented so operators running several instances size it accordingly.
- **Checkout/checkin**: `ExecuteAsync` checks out an idle warm worker if one exists for the key
  (send request, await response — now ~80ms instead of ~1.8s); if none is idle, spawn a **new**
  persistent worker on demand (first query on that worker pays the same ~1.8–3s cold cost as
  today, but every subsequent query on that same key is warm). After a successful call, return
  the worker to the idle pool (unless recycling — see below) instead of killing it.
  Optionally, speculatively pre-spawn the next idle worker immediately after checkout so the
  *next* call likely finds one ready even under back-to-back calls.
- **Per-query timeout**: enforce via the existing `CancellationToken` plumbed into
  `ExecuteAsync`; on timeout or any protocol-level failure, `Kill(entireProcessTree: true)` the
  worker (matching today's `KillIfRunning` behavior) and drop it from the pool rather than
  recycling it — never reuse a worker whose last call did not cleanly complete.
- **Recycling**: retire (send shutdown, or kill if unresponsive) and remove a worker after it has
  served **N queries** (e.g. default 50, configurable) or after an **idle timeout** (e.g. 5
  minutes unused), whichever comes first. A retired worker is not replaced until the next
  checkout miss, keeping steady-state process count low.
- **Shutdown**: on MCP server shutdown, send `shutdown` to all pooled workers with a short grace
  period, then kill any still-running.

### 4.4 Rollout

1. Ship behind `QueryExecution:Mode=Pooled`, default `Auto` unchanged (still `OutOfProcess`).
2. Add config: `QueryExecution:PoolMaxWorkersPerTarget` (default small, e.g. 2),
   `QueryExecution:PoolMaxTotalWorkers` (default small, e.g. 8 — see the global-cap and
   multi-instance notes in §4.3; operators running several MCP server instances/worktrees
   concurrently should size this down accordingly),
   `QueryExecution:PoolMaxQueriesPerWorker` (default e.g. 50),
   `QueryExecution:PoolIdleTimeoutSeconds` (default e.g. 300; the worker's own self-terminating
   idle timeout, §4.2, defaults to 2× this value).
3. Document in [Query execution](query-execution.md) alongside the existing `Mode` table, with an
   explicit callout of the isolation tradeoffs from §3 above, the global/per-instance worker
   caps, and the multi-instance (multiple concurrent MCP servers/worktrees) caveat from §4.3, so
   operators can make an informed choice.
4. Once field experience (or CI soak testing) shows recycling/timeout handles hangs and memory
   growth acceptably, consider flipping `Auto` to prefer `Pooled` — a later, separate decision,
   not part of this initial rollout.

### 4.5 Testing strategy

Extend the pattern already used in
[`OutOfProcessRoslynQueryExecutorTests.cs`](../../tests/DotnetEfCoreMcp.Server.Tests/Querying/OutOfProcessRoslynQueryExecutorTests.cs)
(builds/uses `tests/Fixtures/SampleApp` + the real built `QueryHost.dll`) with new tests for:

- **Correctness parity**: the same queries produce identical results via `Pooled` and
  `OutOfProcess` modes (differential test against existing fixtures).
- **Warm-path speedup**: a smoke-level timing assertion that the 2nd+ call on a pooled key is
  meaningfully faster than the 1st (loose bound, e.g. `<` half of the 1st call's time, to avoid
  environment-timing flakiness while still catching a regression that defeats pooling entirely).
- **Timeout + kill-and-replace**: a query that hangs (e.g. `Task.Delay(Timeout.Infinite)` in a
  test-only query) is killed at the configured timeout, the pool recovers, and the *next* query
  on the same key still succeeds (via a freshly spawned worker).
- **Recycling**: a worker configured with a tiny `PoolMaxQueriesPerWorker` (e.g. 2) is retired
  and replaced after its Nth query; verify process identity (e.g. PID) changes across the
  recycle boundary.
- **Target-rebuild invalidation**: after "rebuilding" the target assembly (touching its
  last-write time), the next call does not reuse a worker warmed against the old build.
- **Worktree isolation**: two "target apps" that are actually the same repo checked out at two
  different paths (i.e. two git worktrees) get independent sub-pools and never share a worker,
  even if their assembly contents are byte-for-byte identical.
- **Global cap and backpressure**: with `PoolMaxTotalWorkers` set to a small test value (e.g. 2)
  and more distinct target-app keys in play than that, verify the pool never exceeds the cap,
  overflow calls still succeed (via one-shot fallback rather than failing or hanging), and an
  LRU idle worker is evicted to make room for a newly-requested key.
- **Worker self-termination**: a persistent worker that is orphaned (e.g. its stdin pipe is
  closed without an explicit shutdown request, simulating a crashed pool manager) exits on its
  own once its self-terminating idle timeout elapses, rather than running indefinitely.
- **Shutdown**: pool disposes/kills all outstanding workers cleanly when the server itself shuts
  down (no orphaned `dotnet` processes left behind — a real risk worth an explicit test given
  child-process lifetime bugs are easy to introduce).

## 5. Alternatives considered and rejected (or deprioritized)

- **ReadyToRun/AOT-compile `DotnetEfCoreMcp.QueryHost`**: would reduce the ~90–250ms raw process
  **startup** cost, which is already a small fraction (~10-15%) of total latency — not the
  ~1.0–1.4s Roslyn-compilation cost, which is JIT/type-load cost incurred while *running*
  `Microsoft.CodeAnalysis.CSharp` itself, not while starting the host process. R2R could plausibly
  shave some JIT time within the compile phase too (Roslyn assemblies pre-JIT'd via R2R avoid
  redundant tiered-compilation work), but this is unmeasured and would need a follow-up
  experiment; even a generous 30-40% cut to the compile phase leaves ~700ms-1s per query, still far
  above the ~80ms pooling achieves. **Verdict: not pursued as the primary fix — the win is
  structurally smaller than pooling's — but worth a cheap follow-up measurement since it composes
  with pooling for the unavoidable "first query on a cold worker" cost.**
- **"Warm standby" (spawn the next one-shot process speculatively while the current query
  executes, but never reuse a process across queries)**: avoids all state-leakage/recycling
  concerns because each process still only ever serves one query — but it does **not** address
  the actual bottleneck, because the *cost is Roslyn compilation happening inside the process*,
  not the wall-clock time to spawn a fresh process. Warming a fresh cold process ahead of time
  does not warm its JIT/Roslyn caches — those only warm up once the process actually starts
  compiling something. This alternative would only help the small (~100-250ms) process-launch
  slice, which isn't the bottleneck. **Verdict: rejected — solves the wrong problem.**
- **In-process execution instead of out-of-process** (`QueryExecution:Mode=InProcess`): already
  exists and is fast (no process-spawn/Roslyn-cold-start cost at all, since the server's own
  process is already warm), but reintroduces exactly the assembly-identity/version-compatibility
  brittleness that motivated building the out-of-process host in the first place (see
  [Query execution alternatives](query-execution-alternatives.md)). Not a substitute for pooling
  when isolation/version-independence is required; remains the right choice only when the target
  app's dependency stack is known to match the server's.

## Why this isn't implemented yet

Per the scope of this investigation, the deliverable is this design document, not the pooled
implementation itself. The pooling change touches process-lifetime management, a new wire
protocol variant, and new failure-mode handling (timeout/kill/recycle) — enough surface area
that it warrants its own reviewed PR with the full test matrix in §4.5, rather than landing as a
byproduct of a latency investigation. The throwaway PoC code used to gather the numbers in this
document has been deleted; the numbers and this design are the durable output.
