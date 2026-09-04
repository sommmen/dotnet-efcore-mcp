# Query execution alternatives and runtime-hosting model

## Summary

The current `run_query` approach is fundamentally an in-process assembly-loading model. It loads the target app assembly into an isolated `AssemblyLoadContext`, then keeps EF Core/provider assemblies in the server's default context so `DbContext` and provider types keep matching identities.

That design works only when the MCP server and target app are aligned on the same framework and EF Core major-version stack. It is brittle across different .NET major versions and across apps with different EF Core provider dependencies.

The more robust model is an out-of-process query host: run user code in the same runtime as the target app, with the target app's own dependency closure, and return only serialized results to the MCP server.

This is the same broad pattern used by LINQPad and by `dotnet ef`.

## Why the current model is brittle

The current implementation in the server does the following:

- loads the target app assembly in a collectible `AssemblyLoadContext`
- resolves the target dependencies locally
- explicitly shares EF Core and provider assemblies back to the default load context to preserve `DbContext` type identity

Relevant evidence in this repo:

- `src/DotnetEfCoreMcp.Server/AssemblyLoading/TargetAssemblyLoadContext.cs`
- `src/DotnetEfCoreMcp.Server/AssemblyLoading/SharedFrameworkAssemblyNames.cs`
- `src/DotnetEfCoreMcp.Server/Querying/RoslynQueryExecutor.cs`
- `src/DotnetEfCoreMcp.Server/Compilation/QueryCompiler.cs`
- `docs/development/assembly-loading.md`
- `docs/development/roslyn-user-query.md`

The important architectural constraint is visible in the shared-framework allowlist: the server is intentionally reusing the host's EF Core and provider assemblies. That is not a neutral implementation detail — it is a compatibility lock.

The practical result is:

- the target app and the MCP server must agree on major runtime and EF Core identity
- different target frameworks (for example .NET 8 vs .NET 9) can surface `FileLoadException`, `MissingMethodException`, or type-identity mismatches
- the approach is difficult to generalize to multiple target runtime versions or provider stacks in one host process

## Why this is hard across .NET 8/9/10

The underlying issue is not just "assembly loading" in the abstract. It is type identity and binding identity.

When a target app is loaded into a different runtime context than the one that owns the EF Core/provider assemblies, the runtime can no longer assume they are the same artifact identity. In practical terms:

- `DbContext` type reference may not match the target assembly's `DbContext`
- EF Core provider types may resolve against the wrong assembly version
- extension methods and service registrations may fail or behave inconsistently

This is why the project's current implementation explicitly shares a narrow set of framework assemblies instead of cleanly isolating everything. That makes the design faster to prototype, but it makes it fragile across major-version boundaries.

## LINQPad approach

LINQPad uses a fundamentally different execution pattern than "load target assembly into the current process".

The design is roughly:

1. generate a real C# class that derives from the target `DbContext`
2. compile it against the target assembly and project types
3. instantiate that generated query class in a dedicated query host
4. execute the user query there
5. return the materialized results to the client

The key architectural point is that the query host is not the same as the current MCP server process. It is a dedicated runtime environment with its own dependency closure.

This lets LINQPad target a specific runtime and a specific app model without having to drag the app's dependencies into the editor process itself.

## `dotnet ef` model

`dotnet ef` follows the same general pattern, but in a more official .NET-hosted form.

The EF tools launch a child process using the target app's runtime configuration:

- `dotnet exec --depsfile ... --runtimeconfig ... ...`
- optionally `--additionalprobingpath` for package/cache probing

This matters because the child process is launched under the target app's runtime, not the tool's own process. The child process then loads the target assembly and performs design-time operations in the correct dependency context.

This is exactly the architecture we want when the query needs to run against the app's real runtime and its real EF Core/provider set.

## Comparison of models

### 1. In-process current model

Pros:

- simple to implement
- easier to debug locally
- good when target app and host server are version-aligned

Cons:

- fragile across .NET major versions
- fragile across differing EF Core/provider versions
- requires explicit assembly sharing and compatibility rules
- not a good general cross-target execution model

### 2. Roslyn-generated query in the same process

Pros:

- closer to LINQPad semantics
- supports real C# language semantics instead of an expression-string parser
- more powerful than a custom dynamic LINQ allowlist

Cons:

- still runs in the same runtime as the MCP server
- still inherits the same assembly identity/compatibility problem
- does not solve the cross-version runtime mismatch by itself

### 3. Out-of-process target-runtime host

Pros:

- the target app owns its runtime and dependency closure
- no forced EF Core/provider assembly sharing across host and target
- supports multiple target runtime profiles cleanly
- aligns with LINQPad and `dotnet ef`

Cons:

- requires IPC and a stable protocol
- adds process startup and lifetime management
- needs serialization and result-shaping logic for returned query results

## Recommended architecture

The most robust option is:

- choose a target app or runtime profile
- launch a dedicated child process for that runtime
- pass the query text and connection metadata to that process over stdin/stdout, named pipes, or a socket
- inside the child process, load the target app's assembly and execute the query in that runtime
- return only the serialized result set to the MCP server

This design preserves the key property we need: the query executes in the target app's runtime, not the MCP server's runtime.

The Roslyn-generated `UserQuery` model should still be used inside the child process. That gives us LINQPad-style C# execution while keeping the runtime identity correct.

In other words:

- out-of-process host solves runtime compatibility
- Roslyn-generated query class solves query semantics
- serialized result return keeps MCP boundaries clean

## Implemented first phase

The first isolated-execution phase is implemented for Roslyn queries. `QueryExecution:Mode`
selects `InProcess`, `OutOfProcess`, or `Auto`; `Auto` currently selects the isolated host until
compatibility fingerprinting is available. Dynamic LINQ remains an in-process execution engine.

The host processes one versioned JSON request from standard input and writes one serialized result
or sanitized error response to standard output. The server generates and validates a request ID.
Connection details are therefore not placed on the command line, and host stderr is not exposed to
MCP callers.

The server launches the host as:

```text
dotnet exec --runtimeconfig <target>.runtimeconfig.json --depsfile <query-host>.deps.json <query-host>.dll
```

The two artifacts intentionally have different owners:

- the **target application's runtimeconfig** selects the runtime/framework appropriate for the
  target application;
- the **query host's depsfile** resolves the host and MCP server dependency closure.

Using the target application's depsfile for the host does not work: it does not contain the
query-host or server assemblies. The initial phase uses a short-lived process per request and does
not yet implement compatibility fingerprinting or a long-lived host.

## Design choice to make explicitly

There are two realistic support models:

- single runtime-major-profile mode: support one runtime family at a time
- multi-runtime-profile mode: support multiple runtime profiles side-by-side (for example .NET 8 app host, .NET 9 app host, etc.)

The second is more future-proof, but it requires that the server maintain a runtime profile map and a dedicated host for each target spec.

## Should the project support both execution modes?

Supporting both an in-process and an out-of-process mode is reasonable, but the selection
criterion should not be the target framework moniker alone. A target built for `net8.0` may still
use a provider or EF Core assembly set that is incompatible with the server, while a target
compiled for another compatible framework may be safe to load in-process.

The safer decision is based on a **compatibility fingerprint** containing at least:

- target runtime/TFM and runtimeconfig framework identity
- EF Core major/minor version
- provider assembly names and versions
- target architecture/platform requirements
- server runtime and loaded EF Core/provider versions

Use in-process execution only when the fingerprint is explicitly proven compatible. Select
out-of-process execution for mismatches, missing metadata, unsupported target frameworks, or
configuration that requires strict isolation. The user should also be able to force a mode for
diagnostics:

```text
QueryExecution:Mode = Auto | InProcess | OutOfProcess
```

`Auto` should fail closed to out-of-process when compatibility cannot be established; it should
not optimistically attempt an in-process load and fall back after partially loading assemblies.

### Maintenance assessment

The main maintenance risk is not having two process-launch options. It is accidentally creating two
different query implementations with different behavior, safety rules, result shaping, and error
handling. That would make every feature and bug fix need to be implemented twice.

The burden remains manageable if the modes share one contract and one execution pipeline:

- one request/response DTO and one `run_query` binding
- one query compiler and query policy
- one result-shaping/serialization implementation
- one timeout, cancellation, row-limit, and error-sanitization policy
- thin in-process and out-of-process adapters around the shared execution core

The out-of-process adapter necessarily adds operational code for process discovery, runtime
selection, IPC, host pooling, health checks, cancellation, and restart behavior. That is a real
cost, but it is localized and buys compatibility that the in-process model cannot provide. The
cost
would become excessive if the project tried to maintain separate Dynamic-LINQ and Roslyn query
semantics in both modes.

### Recommendation

Implement both modes behind a single abstraction, but make `Auto` prefer out-of-process for
cross-runtime or uncertain targets. Keep in-process as an optimization for already-compatible
targets, not as the architectural default. This gives fast local execution where it is safe while
retaining a reliable escape hatch for .NET 8/9/10 and differing EF Core/provider graphs.

The first production release of the out-of-process path may reasonably support only
`OutOfProcess` and `Auto` (where `Auto` selects out-of-process); add in-process auto-selection
after compatibility fingerprinting and parity tests are in place. This avoids making a fragile
compatibility guess part of the initial rollout.

## Integration plan

### Phase 1: define the shared execution boundary

Extract an internal `IQueryExecutionBackend` (or equivalent) whose input is the resolved target
assembly/context, query request, connection options, cancellation token, and execution limits. Its
output should be the existing internal query result shape rather than MCP-specific JSON.

Keep these concerns above the backend:

- MCP request validation and binding
- mode selection
- authorization/read-only policy
- timeout and cancellation policy
- final response formatting

Keep these concerns inside the backend:

- target-context creation
- query compilation/invocation
- EF query execution
- bounded materialization
- conversion to the shared internal result shape

### Phase 2: stabilize the in-process backend

Move the existing `RoslynQueryExecutor` behind the shared interface without changing its behavior.
Add a compatibility-fingerprint service that inspects the target `.deps.json`,
`.runtimeconfig.json`, target assembly references, and loaded server assemblies. Initially make
the service conservative: return `Compatible`, `Incompatible`, or `Unknown`, with diagnostic
reasons.

`Auto` should select in-process only for `Compatible`; both `Incompatible` and `Unknown` should
select out-of-process.

### Phase 3: turn the PoC into a production query host

Promote the PoC's child process into a dedicated query-host project. Keep it free of a compile-time
reference to the arbitrary target application's types. Launch it using the target app's
runtimeconfig/deps graph, as demonstrated in
`poc/OutOfProcessQueryHost/`.

Use a versioned IPC protocol containing:

- protocol version and request id
- target assembly/context identity
- query text and compilation options
- connection identifier or server-side connection reference (never unredacted secrets in logs)
- timeout, cancellation, and result limits

The host should return structured success/error messages, not scrape console text. Use a named pipe
or loopback socket for a long-lived host; keep stdin/stdout as a simple bootstrap or fallback
transport.

### Phase 4: add lifecycle and performance controls

Implement one warm host per runtime profile initially, with a bounded pool only if concurrent
requests require it. Add startup timeout, per-request timeout, cancellation propagation, health
checks, idle expiration, maximum requests per host, and restart-on-crash behavior.

Instrument cold startup, host reuse, Roslyn compilation, database execution, serialization, and
total duration separately. This validates that the out-of-process path is paying only the expected
startup/IPC cost.

### Phase 5: parity and rollout

Run the same contract tests against both backends for:

- scalar, sequence, projection, ordering, paging, and aggregate queries
- cancellation and command timeout
- provider failures and sanitized diagnostics
- result limits and serialization
- target assembly load failures
- host crashes and protocol errors

Roll out in this order:

1. `OutOfProcess` opt-in for the PoC-derived host
2. `Auto` defaulting conservatively to out-of-process
3. in-process selection for proven-compatible fingerprints
4. optional explicit `InProcess` override with a clear warning that compatibility is caller-forced

Do not remove the existing in-process implementation until parity, lifecycle, and performance
tests show that the child host is reliable. Once out-of-process is the default and stable, the
in-process backend can remain as a compatibility optimization or be removed if its complexity no
longer justifies its speed advantage.

## Performance considerations

The out-of-process model adds overhead, but most of that cost is process startup rather than query execution. The database still performs the same EF Core translation and SQL execution; the additional work is runtime startup, query compilation, IPC, and result serialization.

### Latency profile

Compared with the current in-process path, a cold out-of-process request typically adds:

- starting the `dotnet` host and loading the runtime
- loading the query host, target assembly, EF Core, and provider assemblies
- compiling the generated Roslyn query
- establishing the IPC request/response

For a query that returns many rows or waits on the database, this startup cost may be a small part of total latency. For a fast query returning a few rows, it can dominate the response time. The design should therefore distinguish cold-start latency from warm query latency rather than measuring only end-to-end time.

### Throughput and resource cost

Starting one process per request gives the strongest isolation but has the worst throughput and highest CPU/memory churn. A long-lived host process per runtime profile is generally preferable: it pays runtime and assembly loading once, then handles multiple requests while preserving the target runtime boundary.

The host must still enforce request isolation. Each request should have a cancellation/timeout boundary, a bounded result size, and a clear failure response. A crashed or unhealthy child must be disposable and restartable; it should not require restarting the MCP server.

### Recommended optimizations

1. **Use a warm host pool.** Keep one or a small bounded number of query-host processes per runtime profile. Do not create an unbounded process per request.
2. **Cache compiled queries carefully.** Cache Roslyn output only when the cache key includes the query text, target assembly identity, runtime profile, relevant referenced assembly identities, and compilation options. Otherwise a cached assembly can execute against the wrong model or dependency set.
3. **Reuse loaded assemblies and metadata.** A warm host can retain the target assembly, EF metadata, Roslyn metadata references, and provider initialization state between requests.
4. **Keep the IPC payload compact.** Send structured request metadata and stream or serialize only the bounded result. Avoid sending compiled assemblies or duplicating large project files over the pipe.
5. **Preserve database-side work.** Apply `AsNoTracking`, server-side projection, deterministic paging, command timeouts, and cancellation before materialization. These usually have a larger effect on query speed than the process boundary.
6. **Measure cold and warm paths separately.** Record host startup, compilation, database execution, serialization, and total duration so regressions can be attributed correctly.

### Performance trade-off

The expected trade-off is higher latency for the first request and modest IPC/serialization overhead for subsequent requests in exchange for runtime correctness and support for multiple .NET/EF Core versions. A warm, pooled host should make steady-state execution close to the target application's native query cost, while a one-process-per-request implementation should be treated as a simple first iteration rather than the final performance design.

## Conclusion

The current model is viable only in a narrow compatibility window. It is not a general approach for cross-version or cross-runtime query execution.

The better architecture is an out-of-process runtime host that runs the query in the target app's own runtime, using a warm, bounded child-process pool plus IPC. This matches the way LINQPad and `dotnet ef` are structured and avoids the core problem: forcing the server to share EF Core/provider assembly identity with a different runtime. The warm-host design limits the execution-speed penalty to startup on cold requests plus small IPC and serialization costs on warm requests.

For this project, the best path is:

- keep the query execution semantics LINQPad-like (real C# query class)
- move the actual execution out of the server process
- run that compiled query in the target app's runtime and dependency graph
- return only the final shaped results
