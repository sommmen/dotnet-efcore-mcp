# Renovate

## Secrets

There are GitHub Actions secrets defined for the self-hosted Renovate run.

- `RENOVATE_TOKEN` — a PAT (not `GITHUB_TOKEN`) so automerge can trigger
  further required workflow runs. Only needed for the rich workflow tier.

### Validating the config

Run this command in the repo root:

```powershell
npx --yes --package renovate -- renovate-config-validator
```

### Automerging

For automerge to actually merge PRs, GitHub's auto-merge feature must be
enabled on the repository, and any protected branch must allow it.

- <https://docs.renovatebot.com/key-concepts/automerge/>
- <https://docs.github.com/en/repositories/configuring-branches-and-merges-in-your-repository/configuring-pull-request-merges/managing-auto-merge-for-pull-requests-in-your-repository#managing-auto-merge>
