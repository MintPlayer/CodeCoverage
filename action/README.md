# Coverage Upload action

Uploads coverage reports from a workflow run to a self-hosted
[Coverage](https://github.com/MintPlayer/CodeCoverage) instance. The action is a thin,
format-agnostic uploader — parsing happens server-side (lcov, Cobertura, JaCoCo, …),
so new report formats need no action release.

Multiple invocations from one workflow run (matrix jobs, split suites) bundle into a
single build keyed by `run_id` + `run_attempt`; the server merges them (max semantics).

## Tokenless (OIDC) — preferred

Public repositories need no secret at all: grant the job `id-token: write` and the
action authenticates with a GitHub-signed OIDC token (audience = the server URL).

```yaml
jobs:
  test:
    runs-on: ubuntu-latest
    permissions:
      id-token: write
      contents: read
    steps:
      - uses: actions/checkout@v4
      - run: dotnet test --collect:"XPlat Code Coverage"
      - uses: MintPlayer/CodeCoverage/action@master
        with:
          url: https://coverage.example.com
          use-oidc: true
          flags: unit
          finish: true   # on the last (or only) upload job
```

## With an upload token

Create a token in the web UI (account page → Upload tokens; account- or repo-scoped)
and store it as a repository secret:

```yaml
      - uses: MintPlayer/CodeCoverage/action@master
        with:
          url: https://coverage.example.com
          token: ${{ secrets.COVERAGE_TOKEN }}
          flags: unit
          finish: true
```

Without `finish`, the server auto-finalizes ~2 minutes after the last upload
(30 minutes max).

## Inputs

| Input | Description |
|---|---|
| `url` | Base URL of the Coverage server (required) |
| `token` | Upload token `covt_…` (account- or repo-scoped, created in the web UI) |
| `use-oidc` | Authenticate with GitHub Actions OIDC instead of a token (`false`; needs `id-token: write`) |
| `files` | Explicit files/globs (comma- or newline-separated); auto-detects well-known names when omitted |
| `directory` | Auto-detection root (default: workspace) |
| `flags` | Comma-separated labels for this upload |
| `name` | Session name (default: job name) |
| `finish` | Finalize the build after this upload (`false`) |
| `fail-ci-if-error` | Fail the step on upload errors (`false`) |
| `disable-search` | Only use explicitly listed `files` (`false`) |

On `pull_request` events the action reports the PR **head** SHA (never the ephemeral
merge commit), and it sends `git ls-files` so the server can match report paths that
carry CI-machine prefixes or unstated source roots.

## README badge

```markdown
[![Coverage](https://coverage.example.com/badge/OWNER/REPO.svg)](https://coverage.example.com/r/OWNER/REPO)
```

The repo page has a ready-to-paste snippet (including the `?token=` for private
repositories, and `?branch=` for a non-default branch).

## Versioning

Pin `MintPlayer/CodeCoverage/action@master` for now; a `v1` tag is cut from master
once the input surface settles — after that, pin `@v1`.

## Development

`npm run build` regenerates `dist/` (committed — node20 actions run the bundle). CI
fails when `dist/` is stale. This folder is consumed as
`MintPlayer/CodeCoverage/action@<ref>`; if it ever moves to the Marketplace it needs
its own repository with `action.yml` at the root.
