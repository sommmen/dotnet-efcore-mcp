# Writing guide

How to phrase each section so a maintainer reading it cold can act.

## Summary

The summary answers "should I care about this?" in the time it takes to read two
sentences. Lead with the goal, not the error — the goal is what tells the reader
whether the failure is in their critical path.

**Weak:**
> `run_query` throws an exception.

No goal, no stakes, no indication whether this is a typo in the query or a
structural failure.

**Strong:**
> I was using `run_query` to validate a LINQ query before shipping it, which is
> what the server's `ServerInstructions` ask agents to do. The query is genuinely
> untranslatable by EF Core, and `run_query` did fail — but with a generic
> message that is identical to the one returned for connection and policy
> errors, so the failure carried no signal about what was actually wrong.

Note the two moves: the goal came first, and the finding was stated precisely
("the error is non-discriminating") rather than vaguely ("it threw").

## Steps to reproduce

Each step is one action with one observable result, written for someone with a
clone of the repo and nothing else.

**Weak:**
> 1. Set up the server
> 2. Run a query with an Include
> 3. It fails

"Set up the server" is three paragraphs of decisions. "A query with an Include"
describes thousands of queries, almost all of which work.

**Strong:**
> 1. Build `tests/Fixtures/SampleApp` (`dotnet build`), which has a `Blog` →
>    `Post` one-to-many.
> 2. Configure a connection whose `AccessPolicy.AllowContexts` includes
>    `SampleApp.Data.BloggingContext`. Leave `QueryExecution:Mode` at `Auto`.
> 3. `load_assembly` with the path to `SampleApp.dll`.
> 4. Call `run_query`:
>    ```
>    contextName: "BloggingContext"
>    query: "context.Blogs.Concat(context.Blogs.Where(b => b.Id > 5)).Include(b => b.Posts).Take(10)"
>    ```
> 5. The call returns the error quoted below. It reproduces every time.

The last sentence matters more than it looks: "every time" versus
"intermittently, maybe 1 in 5" sends triage down completely different roads.

## Expected vs Actual

Expected is where reports quietly go wrong. "It should work" is not a
specification — the maintainer still has to decide what "work" means. Say what
output you wanted and, where you can, source it: quote EF Core's own exception,
cite the README line that promised the behavior, or point at the tool's
documented contract.

**Weak:**
> Expected: it works. Actual: error.

**Strong:**
> **Expected:** an error naming the untranslatable construct, the way EF Core's
> own exception does: "Unable to translate a collection subquery in a projection
> since either parent or the subquery doesn't project necessary information…"
>
> **Actual:**
> ```
> Query execution failed.
> ```
> Identical to the message returned when the connection name is wrong, which is
> how I initially misdiagnosed it.

Paste actual output verbatim inside a fenced block. Do not tidy it, trim it, or
re-type it from memory — the exact wording is often the thing that lets the
maintainer grep straight to the line that produced it. Scrub paths and
credentials; change nothing else.

A fenced block is a claim that this is what the software actually printed, so
only put real output in one. If you reconstructed the output from the server's
known response shape because the reporter pasted none, say so right next to it —
"reconstructed from the documented response shape; the reporter did not paste
the raw output." The maintainer can still use it, but will not waste time
grepping for a string that was never emitted.

## Impact

Converts a curiosity into a priority. Answer: who is blocked, how badly, and is
there a workaround?

**Weak:**
> This is a serious bug and should be fixed ASAP.

Severity assertions carry no information; everyone thinks their bug is serious.

**Strong:**
> This affects anyone running the default `Auto` mode, which is most users.
> Because `preview_query_sql` is also unavailable in `Auto` (#85), there is no
> path to a specific translation error in the default configuration. The
> workaround — switching to `InProcess` — trades away the process isolation that
> made `Auto` the default, so it is not one I would recommend to others.

A named workaround with its cost is worth more than any severity label. It tells
the maintainer how long users can live with this.

## Notes / possible cause

Optional, and genuinely better omitted than guessed at. If you do have a lead,
mark its confidence explicitly so the maintainer can weight it:

> I have not traced this, so treat it as a guess: `PreviewSqlAsync` appears to
> build the `IQueryable` in-process to call `ToQueryString()`, while
> `ExecuteRoslynAsync` already compiles caller-supplied expressions in the child
> process. If preview could reuse that path, it would work in `Auto` too.

The hedge is not false modesty — it is the thing that lets the maintainer
discard your theory cheaply if it is wrong, instead of spending an hour
confirming it because you sounded certain.

## Ergonomics issues specifically

When nothing is broken but something is awkward, the trap is dressing friction
up as a bug. It reads as inaccurate and gets triaged as "works as designed."

Describe the friction honestly, then propose a direction:

> `AccessPolicy` being mandatory is the right call — I would not want an
> implicit default. But the first tool call after a fresh install fails with a
> configuration exception, which reads like a broken install rather than an
> unfinished setup. If `list_contexts` surfaced "connection `main` has no
> `AccessPolicy`; add `AllowContexts` naming your DbContext" before the first
> query, the same safety would cost one confused session less.

This grants the design intent, names the cost precisely, and proposes something
cheap. Label it `enhancement`, not `bug`.

## Tone

Write to a maintainer who is on your side and short on time. Frustration is
legitimate and does not need hiding, but it should go into precision rather than
adjectives — "this error is identical to three unrelated errors" lands harder
than "this error is useless", and it is also actionable.

Avoid:
- Rhetorical questions ("why would anyone design it this way?")
- Demands and deadlines
- Speculation about competence
- Padding ("I hope this helps, sorry if this is a dumb question, thanks so much")

Include:
- Exact names, exact versions, exact error text
- What you already tried and ruled out — this is high-value and usually omitted
- Explicit uncertainty where it exists
