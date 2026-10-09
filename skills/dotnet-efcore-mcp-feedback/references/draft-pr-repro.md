# Draft PR reproduction

For failures that prose cannot pin down. Most issues do not need this — an
unnecessary repro project is a maintenance cost for someone else, and a written
repro that works is strictly better because it costs the maintainer nothing to
read.

## A repro PR proves a symptom; it must not attempt a fix

Worth being unambiguous about, because conflating the two is how a repro PR
backfires.

The PR carries a project that **fails**. That is its entire job. It does not
touch `src/`, it does not change behaviour, and it does not encode a theory
about the cause. If you have a theory, it belongs in the issue, marked as a
hypothesis.

The reason is a specific failure mode: a PR that looks like a fix invites the
maintainer to evaluate the fix rather than the bug. If your fix is wrong — and
when the root cause is still ambiguous it usually is — the likely outcome is
that the PR gets closed as not-the-right-approach and the issue gets closed
along with it, while the bug survives untouched. A repro that only demonstrates
the failure cannot fail that way, because there is nothing to disagree with.

This also means **ambiguous root cause is a reason to open the repro PR, not a
reason to avoid it.** When you cannot tell which of two causes is responsible,
a fixture that isolates the failure is precisely the instrument that settles
it — and it is worth more coming from you than from the maintainer, because you
are the one who can see the failing system.

## When it is worth it

Open a draft PR when the failure depends on a model configuration that takes
more than a paragraph to describe accurately:

- A specific inheritance strategy (TPH/TPT/TPC) with a particular discriminator.
- Owned types, value converters, shadow properties, or composite keys.
- A multi-project layout — context in one assembly, entities in another — where
  the layout *is* the bug.
- Provider-specific mappings that behave differently per provider.
- An assembly-loading or scaffolding failure where the project structure is the
  subject.

The honest signal: you are writing "you would need a context where…" and the
sentence keeps growing. At that point, a project the maintainer can `dotnet
build` is worth more than another paragraph.

Do **not** open one for a single untranslatable query, a confusing error
message, a docs fix, or anything `tests/Fixtures/SampleApp` already covers.

## Scaffolding

Keep it minimal and obviously disposable. The maintainer should be able to
delete it after fixing the bug without losing anything.

```
tests/Fixtures/Repro<ShortName>/
├── README.md                    # what to run, what you saw
├── Repro<ShortName>.csproj
├── <Name>Context.cs
└── Entities.cs                  # only the entities the failure needs
```

Model it on `tests/Fixtures/SampleApp` — same TFM and EF Core version unless the
versions are themselves the bug, in which case say so prominently in the README.

The README carries the weight. It should contain:

```markdown
# Repro: <one-line description>

Reproduces <issue link>.

## Build

    dotnet build tests/Fixtures/ReproTphInclude

## Reproduce

1. `load_assembly` with `tests/Fixtures/ReproTphInclude/bin/Debug/net9.0/ReproTphInclude.dll`
2. `run_query` with:
   ```
   contextName: "ReproContext"
   query: "<the exact query>"
   ```

## Observed

```
<verbatim output>
```

## Expected

<one or two sentences>
```

## Opening the PR

Branch from the default branch, add only the repro, and open it as a draft:

```bash
git switch -c repro/tph-include-translation
git add tests/Fixtures/ReproTphInclude
git commit -m "test(fixtures): add repro project for TPH Include translation failure"
git push -u origin repro/tph-include-translation

gh pr create --repo sommmen/dotnet-efcore-mcp --draft \
  --title "repro: TPH Include translation failure" \
  --body "Minimal reproduction for #<issue-number>. Not intended to merge as-is — evidence only.

Build: \`dotnet build tests/Fixtures/ReproTphInclude\`
Then follow the steps in the project README."
```

Notes on the mechanics:

- **Draft, always.** It signals "evidence, not a proposed fix" and stops the
  maintainer wondering whether they are expected to merge it.
- **Say it is not meant to merge** in the body. Draft status implies it; stating
  it removes all doubt.
- **The commit message follows the repo's Conventional Commits convention**
  (`test(fixtures): …`), because this one is a real commit. Issue titles do not
  need the prefix; commits and PR titles do.
- **File the issue first** so the PR can reference its number, then edit the
  issue to link back. Two-way links mean neither can be found without the other.

## Linking from the issue

Add a short section — and keep the written steps regardless. An issue that says
only "see the PR" is not self-contained, which defeats the point.

```markdown
## Reproduction project

The model configuration this depends on is awkward to describe precisely, so
there is a minimal repro in #<pr-number> (draft, not intended to merge):
`tests/Fixtures/ReproTphInclude`. Build it and follow its README; the steps
above describe the same thing in prose.
```

## If you cannot push

Contributors without write access cannot push a branch to the repo. Either fork
and open the draft PR from the fork (`gh repo fork --remote`), or — if a fork is
not appropriate — inline the whole repro into the issue: the `.csproj`, the
context, the entities, each in its own fenced block with its filename as a
heading. Verbose, but still self-contained, which is the property that matters.
