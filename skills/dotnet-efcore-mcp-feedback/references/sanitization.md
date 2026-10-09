# Sanitization and generalization

Two separate jobs, often confused:

- **Sanitization** removes things that must not be public — credentials, host
  names, internal identifiers.
- **Generalization** replaces things that are *private but structurally
  important* with neutral equivalents that keep the structure.

Sanitization alone produces an issue full of `[REDACTED]` that nobody can act
on. Generalization alone leaks. You need both, in that order: strip the secrets,
then re-express what remains in terms anyone can run.

## Never include

| Category | Examples |
|---|---|
| Credentials | Connection strings, passwords, API keys, tokens, `user-secrets` values |
| Infrastructure | Server/host names, IPs, ports, internal DNS, container names |
| Private identifiers | Employer/client names, internal project or product names, ticket IDs, Slack/Teams links |
| Local paths | `C:\Repos\acme-billing\...`, `/home/jsmith/work/...` — replace with `<project-root>/bin/Debug/net9.0/MyApp.dll` |
| Real data | Customer names, emails, order IDs, anything from an actual row |
| Proprietary code | Your real `DbContext`, your real query, your real entity classes |
| Session artifacts | Agent transcripts, chat logs, session IDs, workspace paths |

Connection strings deserve a second pass specifically. They hide inside error
messages and stack traces, and a `Password=` can survive a careless copy-paste
into a code fence. Reread every pasted block looking for one.

### If a credential was already pasted to you

Keeping it out of the issue is necessary but not sufficient. A password pasted
into a chat has already left the user's control — it is in a transcript, and
possibly in logs and model context beyond your reach. Tell the user plainly that
it should be rotated, and do it at the point you notice rather than burying it
at the end of a long summary where it reads as a formality.

This is uncomfortable to raise and easy to skip, which is exactly why it needs
saying: the person who pasted it is focused on their bug and is not thinking
about the credential at all.

## Safe and valuable — keep these

| Category | Examples |
|---|---|
| Public API surface | Tool names (`run_query`), parameter names, config keys (`QueryExecution:Mode`) |
| Verbatim server errors | After scrubbing paths and connection strings from them |
| Version facts | Server version, EF Core version, provider, TFM, SDK, OS |
| Public framework text | EF Core exception messages, provider error codes |
| Repo-internal references | File paths inside `dotnet-efcore-mcp` itself, issue numbers, `SampleApp` entity names |
| Structural shape | Cardinality, key types, nullability, inheritance strategy, owned types |

## Rename every type and property name, by default

This is the rule that gets rationalized away, so it is worth stating plainly:
**rename the reporter's type and property names even when they look generic.**

The tempting reasoning goes "`Document` and `InvoiceDocument` are such ordinary
words that publishing them identifies nobody." Sometimes that is true. The
problem is that you cannot tell from inside the codebase which names are
ordinary and which are distinctive, because every name looks ordinary to someone
who has been reading it all day. `ProductSku` feels generic; it is also a
literal, greppable string from a private schema, and a reader who already has a
guess about the employer now has a confirmation.

The asymmetry settles it. Renaming costs you one minute and loses nothing,
because the structure is what reproduces the bug — not the nouns. Guessing wrong
costs something you cannot take back, since an issue is public and indexed the
moment it exists.

So: rename, and spend your judgement on the part that actually needs it —
deciding which *structure* to keep.

**Do it as a first step, not a final pass.** Write the mapping down before you
draft anything, then build the repro from the new names:

| Theirs | Yours |
|---|---|
| `Acme.Documents.Data` / `.Domain` | `Repro.Data` / `Repro.Domain` |
| `Document` (abstract base) | `Record` (abstract base) |
| `ContractDocument` | `AlphaRecord` |
| `InvoiceDocument` | `BetaRecord` |
| `DocumentKind` (enum) | `RecordKind` (enum) |

Note that `Document` → `Record` is in the table even though "Document" is about
as generic a word as exists. That is the point: the rule is only useful if it
applies without you adjudicating each name, because the adjudication is where it
breaks down. Deciding case-by-case means talking yourself into keeping the ones
that feel harmless, which is precisely the set you cannot evaluate.

Renaming last does not work either. Once a draft is written, every name in it
has a reason to be there, and reviewing your own prose for names you chose
deliberately is the weakest possible check.

## Generalizing a domain model

Keep whatever the bug depended on; discard the rest. The test is: if you change
this detail, does the bug still reproduce? If yes, it is domain colour — drop
it. If no, it is load-bearing — keep it, renamed.

**Before (leaks, and the maintainer cannot run it):**

```csharp
var logs = _db.AuditLogEntries
    .Where(x => x.TenantId == tenantId && x.ObjectType == "Invoice")
    .Concat(_db.AuditLogEntries.Where(x => x.ObjectType == "CreditNote"))
    .Include(x => x.ActingUser)
    .Include(x => x.MessageArguments)
    .ToListAsync();
```

**After (same bug, runnable by anyone):**

```csharp
// Shape that matters: Include over a Concat of two queries on the same entity,
// where the included navigation is a collection.
// SampleApp's Blog -> Post is this shape.
var result = context.Blogs
    .Where(b => b.Id > 10)
    .Concat(context.Blogs.Where(b => b.Rating > 3))
    .Include(b => b.Posts)
    .ToList();
```

What survived: `Include` over `Concat`, the collection navigation, two filtered
subqueries on one entity. What went: tenant scoping, object-type discriminators,
the second `Include`, the business meaning. None of it was load-bearing, and all
of it was private.

When you generalize, say so in the issue — one line is enough:

> Reproduced here against a `Blog`/`Post` one-to-many; the original was a
> different domain with the same shape.

This tells the maintainer the repro is a translation, not a transcript, so a
small discrepancy in their run does not read as "cannot reproduce."

## Deciding what is load-bearing

Not always obvious. Some heuristics for EF Core issues specifically:

- **Usually matters:** relationship cardinality, whether a navigation is a
  collection, inheritance strategy (TPH/TPT/TPC), owned types, value converters,
  composite or non-integer keys, nullability of the joined column, the provider.
- **Usually does not matter:** entity and property names, how many unrelated
  properties exist, the business purpose, the number of rows, the specific filter
  values, which DI container wired it up.
- **Test empirically when you can.** If the repo's `SampleApp` is available,
  reproduce against it before filing. A repro you have actually run is worth far
  more than one you reasoned your way to, and if it *doesn't* reproduce there,
  that is itself important information: it means something in your model is
  load-bearing and you have not identified it yet. Say so in the issue.

## Final pass

Before filing, reread the draft as a stranger and ask:

1. Does any string here identify a person, company, product, or machine?
2. Could any value here be a credential?
3. Is there a path that only exists on my machine?
4. Could someone with only a clone of the repo follow these steps?

If 1–3 are all no and 4 is yes, it is ready.
