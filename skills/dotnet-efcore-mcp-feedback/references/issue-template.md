# Issue template

The structure below is what a maintainer can triage without asking a follow-up
question. Omit sections that genuinely do not apply — a documentation typo needs
no environment table — but do not omit a section because the information was
inconvenient to gather.

## Structure

```markdown
## Summary

One or two paragraphs: what you were trying to do, what the server did instead,
and why that mattered. Written so someone who has never seen your project
understands the situation.

## Environment

| | |
|---|---|
| Server version | 0.1.0-preview.42 |
| Install method | .NET global tool |
| `QueryExecution:Mode` | `Auto` (default) |
| Provider | SQL Server |
| EF Core / TFM | EF Core 9.0.0 / net9.0 target |
| .NET SDK | 10.0.100 |
| OS | Windows 11 |
| MCP client | VS Code |

## Steps to reproduce

1. Build a project containing a `DbContext` with a one-to-many relationship
   (`Blog` → `Post` in `tests/Fixtures/SampleApp` is exactly this shape).
2. `load_assembly` with the path to its build output.
3. Call `preview_query_sql` with:
   ```
   contextName: "BloggingContext"
   query: "context.Blogs.Concat(context.Blogs.Where(b => b.Id > 5)).Include(b => b.Posts)"
   ```

## Expected

The tool returns the generated SQL, or an EF Core translation error naming the
unsupported construct.

## Actual

```
<verbatim output, unedited>
```

## Impact

Who this blocks and how badly. One or two sentences.

## Notes / possible cause

Optional. Your hypothesis, clearly marked as a hypothesis, with any code path
you suspect. Omit entirely if you have no lead — a confident wrong diagnosis is
worse than none.
```

## Worked example

```markdown
## Summary

`run_query` correctly rejects LINQ that EF Core cannot translate, but the error
it returns is the same generic message it returns for several unrelated failure
modes. An agent cannot tell "your query is untranslatable" from "the connection
is misconfigured", so it has no way to decide whether to rewrite the query or
fix the setup — and in practice it tends to do neither and give up.

## Environment

| | |
|---|---|
| Server version | 0.1.0-preview.42 (`dotnet tool list --global`) |
| Install method | .NET global tool |
| `QueryExecution:Mode` | `Auto` (default, not overridden) |
| Provider | SQL Server |
| EF Core / TFM | EF Core 9.0.0, target project `net9.0` |
| .NET SDK | 10.0.100 |
| OS | Windows 11 |
| MCP client | VS Code |

## Steps to reproduce

1. Build any project with a `DbContext` exposing a one-to-many relationship.
   `tests/Fixtures/SampleApp` (`Blog` → `Post`) works unmodified.
2. Configure a connection with an `AccessPolicy` allowing that context, and
   leave `QueryExecution:Mode` at its default.
3. `load_assembly` pointed at the build output.
4. `run_query` with an `Include` applied over a set operation — a construct EF
   Core cannot translate:
   ```
   contextName: "BloggingContext"
   query: "context.Blogs.Concat(context.Blogs.Where(b => b.Id > 5)).Include(b => b.Posts).Take(10)"
   ```

## Expected

An error that names the untranslatable construct, so the caller knows to rewrite
the query. EF Core's own exception for this case is specific and actionable:

> Unable to translate a collection subquery in a projection since either parent
> or the subquery doesn't project necessary information required to uniquely
> identify it …

## Actual

```
Query execution failed.
```

The same string is returned when the connection name is wrong and when the
access policy denies the context, so it carries no signal about which of the
three happened.

## Impact

The server's own `ServerInstructions` tell agents to validate LINQ through this
server before trusting it. This class of bug — valid-looking C# that EF Core
cannot translate — is precisely what that validation is for, and it is the class
that reaches production as an HTTP 500. An error this generic means the
validation pass runs and reports nothing useful.

`preview_query_sql` would be the natural alternative, but it is unavailable in
`Auto` mode (see #85), so in the default configuration there is no path to a
specific answer.

## Notes / possible cause

I have not read the execution path closely, so treat this as a guess: the
out-of-process executor may be catching the provider exception at a boundary and
replacing it with a generic message rather than marshalling the inner exception
type and text back to the caller. If the inner `InvalidOperationException` text
could survive that hop — even truncated — the error would become actionable.
```

## What makes this example work

- **The repro uses `Blog`/`Post`.** Those are the repo's own fixture entities.
  The maintainer's setup cost is a `dotnet build` they already run.
- **The expected result is sourced, not invented.** It quotes EF Core's real
  exception text, so the maintainer can confirm the gap without guessing what
  the reporter wanted.
- **The impact names the consequence.** Not "this is bad" but "the validation
  pass this server exists to provide runs and reports nothing useful."
- **The hypothesis is hedged and specific.** "I have not read the execution path
  closely" plus a concrete, checkable suggestion.
- **No private detail survived.** The original failure was in an audit-log
  endpoint with a `Concat` over scope subqueries. What mattered structurally —
  `Include` over a set operation — is preserved exactly. Everything else is gone.
