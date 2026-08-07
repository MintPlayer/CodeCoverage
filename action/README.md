# Coverage Upload action

Uploads coverage reports from a workflow run to a self-hosted
[Coverage](https://github.com/MintPlayer/CodeCoverage) instance. The action is a thin,
format-agnostic uploader — parsing happens server-side, so new report formats need no
action release.

Multiple invocations from one workflow run (matrix jobs, split suites) bundle into a
single build keyed by `run_id` + `run_attempt`; the server merges them (max semantics).

```yaml
jobs:
  test:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - run: dotnet test --collect:"XPlat Code Coverage"
      - uses: MintPlayer/CodeCoverage/action@develop
        with:
          url: https://coverage.example.com
          token: ${{ secrets.COVERAGE_TOKEN }}
          flags: unit
          finish: true   # on the last (or only) upload job
```

Without `finish`, the server auto-finalizes ~2 minutes after the last upload
(30 minutes max).

| Input | Description |
|---|---|
| `url` | Base URL of the Coverage server (required) |
| `token` | Upload token `covt_…` (required; org- or repo-scoped, created in the web UI) |
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

## Development

`npm run build` regenerates `dist/` (committed — node20 actions run the bundle). CI
fails when `dist/` is stale. This folder is consumed as
`MintPlayer/CodeCoverage/action@<ref>`; if it ever moves to the Marketplace it needs
its own repository with `action.yml` at the root.
