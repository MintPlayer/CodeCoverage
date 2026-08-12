# Coverage — Development Plan

Companion to [PRD.md](PRD.md). Milestones are ordered so that every stage produces something demonstrable, upstream PRs are unblocked early, and the layering rule (generic → upstream repo) is respected. Target: **one PR per repo** per milestone group.

Legend: 🟦 Coverage repo · 🟩 MintPlayer.Spark PR · 🟨 mintplayer-ng-bootstrap PR · 🟪 coverage-action repo

---

## M0 — Upstream groundwork 🟩

Goal: unblock everything else; one Spark PR.

0. **Confirm the base branch** — the local checkout is on `security-audit` (one commit ahead of master); agree with the maintainer where M0 branches from, and whether the open **R4-H1** finding (row-level auth missing on `/spark/queries/{id}/execute` and `/stream`) is fixed in this PR or worked around app-side (PRD §12 1b).
1. **Verify & fix the typed-webhook queue-name bug**: boot WebhooksDemo; if `GitHubWebhookMessage<TEvent>` queue names (`FullName` of a closed generic → contains `[ ] , =`) indeed fault `MessageSubscriptionWorker.IsValidQueueName`, fix by sanitizing/hashing generic queue names (or a `[MessageQueue]`-style naming hook) + regression test.
2. **New library `MintPlayer.Spark.Authorization.ApiTokens`** (name TBD — align with maintainer taste):
   - `SparkApiToken` document: hash-as-id, scope claims, created-by, expiry, revocation; store beside `UserStore` conventions (compare-exchange not needed — hash id is unique by construction).
   - Token format `covt_`-style prefix + 256-bit urlsafe random; only SHA-256 stored; value returned once.
   - `ApiTokenAuthenticationHandler` (`Authorization: Bearer <token>` / `Token <token>`) → `ClaimsPrincipal` with scope claims; registered through the existing `configureProviders: Action<IdentityBuilder>` hook.
   - Issuance/list/revoke endpoints under `/spark/auth/tokens` (XSRF-protected, cookie-authenticated).
   - Consuming app supplies the scope vocabulary; library stays domain-agnostic.
3. **External-login popup fix**: `external-login` must propagate `popup` to the callback URL so the `postMessage` handshake fires; fix the demo's listener leak.
4. **ng-bootstrap dependency bump** in Spark: `^22.4.0` → current (22.13.x), adding new peer deps (`@mintplayer/web-components ^2`, `lit ^3.3`); fix fallout in ng-spark/ng-spark-auth and demos.
5. Opportunistic doc-drift fixes (only the cheap ones listed in PRD §10.4).

**Exit criteria**: WebhooksDemo boots clean with typed recipients receiving events; a demo app can mint and authenticate with an API token; Spark tests green.

## M1 — Scaffold the Coverage app 🟦

Copy the WebhooksDemo anatomy (the 27-item checklist from the investigation):

1. `Coverage.Library` (entities: Account, Repository, UploadToken-scope holder if app-side, Commit, Build) + `Coverage` host + `ClientApp` (shell, home page), `App_Data/Model/*.json` via `--spark-synchronize-model`, `Synchronize` launch profile (WebhooksDemo lacks it — HR is the reference).
2. Consume Spark via **published NuGets** (`10.0.0-preview.41`) and `@mintplayer/ng-spark*` from npm — Coverage is the first out-of-tree consumer; upstream any packaging bugs found. Use `MintPlayer.Spark.Testing` (embedded RavenDB; needs `RAVENDB_LICENSE`) for integration tests. Test trap to remember: hand-written `session.Query<TView, TIndex>()` needs `.ProjectInto<TView>()` or index-computed fields come back null.
3. GitHub App (dev) + OAuth login + webhooks wired (`installation`, `repository`, `push`, `pull_request` recipients that upsert Accounts/Repos/Commits); smee.io dev tunnel.
4. Org/repo visibility sync on login (port `OrganizationAccessService` pattern; consider promoting to Spark later). Fix its two known flaws when porting: use `IHttpClientFactory` instead of a bare `HttpClient` per call, and cache beyond per-request (TTL + manual resync) to stay under GitHub rate limits.
5. docker-compose + Dockerfile (WebhooksDemo template, pinned RavenDB).

**Exit criteria**: sign in with GitHub → home page lists your orgs/repos (empty coverage), webhook keeps repo list current.

## M2 — Ingestion pipeline 🟦

The heart of the product; testable without any UI.

1. Normalized model (`Line {Number, Hits?, Status}` etc. — PRD §5) + merge (max semantics, per-session).
2. `ICoverageParser` + sniffing factory (root-element/text dispatch). Parsers: **LCOV**, **Cobertura** (then JaCoCo in M2.5). Extensive fixture-based tests (real files from coverlet, nyc, coverage.py, gcovr, lcov 1.x+2.x).
3. Path normalizer: rootDir strip, slash unification, Cobertura `<source>` resolution, `fileList` suffix-matching fallback, unmatched-bucket.
4. `POST /api/uploads` (multipart: metadata + gzipped files + fileList) authenticating via API token (M0 lib); store raw files as RavenDB attachments on the Build; Build/Session bookkeeping keyed `(repoId, sha, runId, runAttempt)`; parse via Spark message-bus recipient.
5. Finalization: explicit `POST /api/uploads/finish` + debounce (~2 min) + timeout (~30 min) via cron/subscription worker; recompute `Commit.CoverageSummary`.
6. Rate limiting on `/api/*`.

**Exit criteria**: `curl` two lcov+cobertura uploads for one fake run → one finalized build with correctly merged per-file line data (verified idempotent under re-upload).

## M3 — GitHub Action MVP 🟪

1. New repo `coverage-action`: node20 + TypeScript + ncc bundle (dist/ committed + CI staleness check).
2. v1 inputs: `url`, `token`, `files`/`directory` globs (auto-detect fallback using Codecov's glob/ignore lists), `flags`, `name`, `finish`, `fail-ci-if-error`.
3. Correct metadata (PR-head SHA, branch, runId/runAttempt, `rootDir`, `git ls-files`).
4. Dogfood: run it in the Coverage repo's own CI (and optionally mintplayer-ng-bootstrap's — both already emit cobertura).

**Exit criteria**: a real workflow uploads real coverage to a deployed dev instance, multiple jobs bundling into one build.

## M4 — Browse UI 🟦

1. **Home** (accounts + aggregate %), **Account** page (repos + latest default-branch coverage), **Repository** page (branch selector, commit list with % and delta).
2. **Commit/build** page: summary header, sessions/flags with parse status, unmatched-files warning, and the **file/folder tree** via `bs-datatable` tree mode (lazy children from a tree-summary endpoint, coverage-% columns with `bs-progress-bar` cells).
3. Custom endpoints + RavenDB static indexes for the commit list and tree aggregation (not Spark generic queries — paging happens in-memory there).
4. Private-repo pages gated on the viewer's synced GitHub access.

**Exit criteria**: click-through org → repo → commit → folder → file list matches uploaded data.

## M5 — File view + code-viewer component 🟨🟦

1. 🟨 **`mp-code-viewer`** in ng-bootstrap (extend `mp-code-snippet`): line numbers, generic per-line annotation API (status class + optional label/count), line anchors, light theme, keyboard/a11y per repo CLAUDE.md; Angular wrapper `bs-code-viewer`; conformance-suite registration; demo page.
2. 🟦 File view page: fetch source from GitHub at view time (installation token, contents: read, ETag cache — we never store source), overlay line coverage (green/red/orange + hit counts), `#L42` deep links.

**Exit criteria**: viewing a covered file for a commit shows highlighted source identical to the report.

## M6 — Badges 🟦

1. `GET /badge/{owner}/{repo}.svg?branch=…` — self-rendered SVG (shields.io-style flat badge is ~30 lines of templated SVG), color scale red→green.
2. Private repos require `&token={BadgeToken}` (scoped, rotatable; wrong/missing → "unknown" badge, never 404).
3. Repo page shows the ready-to-paste markdown snippet; settings allow badge-token rotation.
4. `Cache-Control: max-age=300` + rate limiting.

## M7 — OIDC tokenless uploads 🟦🟪

1. 🟦 JWT bearer validation (`Authority = token.actions.githubusercontent.com`, `aud` = our base URL); claims (`repository`, `repository_id`, `sha`, `run_id`, `run_attempt`) override body metadata.
2. 🟪 Action: `use-oidc` input (default on when `id-token: write` available and not a fork), `core.getIDToken(url)`.
3. Policy: public repos may auto-provision on first OIDC upload; private repos must be known (App installed).

## M8 — Dependency upgrade + coverage diagram 🟦 (UNBLOCKED 2026-08-10)

The upstream halves landed: Spark#231 (→ `10.0.0-preview.42`, `ng-spark-auth 22.1.0`) and
ng-bootstrap#401 (→ `22.14.0` charts). All remaining work is in this repo.

**Step 1 — upgrade (required):**
1. Bump all `MintPlayer.Spark.*` NuGets `10.0.0-preview.41` → `10.0.0-preview.42`.
2. ClientApp: `@mintplayer/ng-spark-auth` → `^22.1.0`; **pin** `@mintplayer/ng-bootstrap` `22.14.0` + `@mintplayer/web-components` `2.11.0` (the old `^22.13.0` caret resolves to 22.14 silently — make it a deliberate commit).
3. Runtime verification: GitHub OAuth sign-in (the new composite default-authenticate scheme must not disturb the cookie path), one `covt_` upload, one OIDC-JWT upload. A "refused by every registered scheme" log warning is cosmetic (see step 2.1).

**Step 2 — upgrade follow-ups (optional, recommended):**
1. Register the **ApiToken** scheme as a Spark credential scheme (`spark.AddCredentialScheme("ApiToken", isAmbient: false)` after moving its registration into the AddSpark callback) — silences the per-upload warning + earns the non-ambient antiforgery exemption. Deliberately do NOT register GitHubOidc (it would widen where workflow JWTs are accepted).
2. Shell: replace the full-page-redirect login workaround with `authService.loginWithProvider('GitHub')` (ng-spark-auth 22.1.0 owns the whole popup handshake incl. blocked/closed/refused paths).
3. ~~Convert `GitHubEventsRecipient` to typed recipients~~ — **deliberately skipped**: the webhook processor broadcasts BOTH the catch-all and the typed envelope per event regardless of subscribers, so converting would only swap which family of unconsumed messages accumulates while splitting one cohesive handler into five classes. Revisit if Spark ever broadcasts only to subscribed queues (possible upstream ask).

**Step 3 — coverage diagram (the feature):**
1. Commit page: `bs-hierarchy-chart` (`layout="sunburst"`, `colorMin≈60`/`colorMax≈80`) fed from a new full-tree endpoint variant returning per-file `HierarchyNode {id: path, value: coverableLines, colorValue: coveredPct}` — folder colors derive upstream (value-weighted mean), no server rollup. `(zoom)` → `openFolder(path)`, `(nodeSelect)` → file view, `[(rootId)]` two-way-bound to the existing folder drill-down so tree and chart stay in sync (that pairing is also the documented WCAG target-size story). Bound column width (aspect-ratio 1 fills width).
2. Headline radial ring: hand-rolled `CoverageRingComponent` (~20 lines) on the public `arcPath` + `colorScale` from `@mintplayer/web-components/charts/core` (`ringGap: 0`) — upstream declined a donut/gauge component; contribute `mp-progress-circle` later only if this shape proves general.
3. Later, once history is queryable: `bs-trend-chart` (with `goal` line) on the repo page; `bs-sparkline` in tables.

## M10 — Adopt the unified code viewer 🟦 (UNBLOCKED 2026-08-11 by ng-bootstrap#402)

[PR #402](https://github.com/MintPlayer/mintplayer-ng-bootstrap/pull/402) extended
`bs-code-snippet` into the full viewer (per-line DOM via subgrid rows, `annotations:
CodeLineAnnotation[]` with `{line, kind, label, secondaryLabel, description}`,
`lineNumbers`, `lineHref`, `activeLine`, `scrollToLine()`, `data-bs-theme`-aware
`light-dark()` theming, roving-tabindex a11y). No separate `mp-code-viewer` exists.
Upstream's own migration checklist for OUR file page: mintplayer-ng-bootstrap
`docs/prd/code-snippet-viewer.md` §12; working coverage-shaped demo under
`apps/ng-bootstrap-demo/.../advanced/code-snippet/`.

1. Pin `@mintplayer/ng-bootstrap` **22.15.0** + `@mintplayer/web-components` **2.12.0**; add
   **`highlight.js@^11.11.1` as a direct dependency** (declared optional peer, but the published
   module has a static `import 'highlight.js/lib/core'` — the Angular build fails to resolve
   without it). Breaking changes in 22.15 are confined to code-snippet (`codeToCopy`→`code`,
   `lineActivate` payload, theme default) — Coverage uses none of it yet, clean upgrade.
2. Replace the hand-rolled renderer in `pages/file/` with `<bs-code-snippet>`:
   map the existing `RenderedLine[]` → `CodeLineAnnotation[]` (`kind` = covered/partial/uncovered,
   `label` = hits ("0×" renders — label shows when present), `secondaryLabel` = branch ratio,
   `description` for the tooltip/SR text); `[lineHref]="(l) => '#L' + l"` (bare fragments are
   rewritten against `location.pathname + location.search`, so `?path=` survives — no routerLink
   needed); tint via `::ng-deep mp-code-snippet::part(annotation-<kind>)` (parts, not CSS vars —
   deliberate upstream deviation). Source-unavailable case: annotations may exceed `code`'s
   extent, so `code: ''` still renders a full gutter.
   **The one silent breaker**: `scrollToTarget()` uses `document.getElementById('L'+n)` which
   returns null into a shadow root — must become `viewer()?.scrollToLine(n)` (viewChild).
   Write our own extension→language map (grammar keys cover cs/ts/html/json/scss/sql/yaml/vb/md;
   razor/fsharp/xaml absent → plain text + console.warn; `canHighlight`/`registerLanguage`
   exported for gating/extending). Layout trap: `code { min-width: max-content }` propagates —
   flex ancestors need `min-width: 0` or phones get body-level horizontal scroll.
3. Cleanup while there: `bs-shell-topbar.directive.ts` is unnecessary — upstream confirmed a plain
   `<div slot="topbar">` works and the directive's "promote upstream" TODO points at nothing;
   delete it in shell.component. Also fix the stale comment in
   `Recipients/GitHubEventsRecipient.cs:14-19` (cites the FIXED queue-name bug; the real reason
   for keeping the catch-all is the dual-broadcast note in M8 step 2.3).

## M9 — Verified backlog (from the 2026-08-12 code-vs-docs audit) 🟦

### Correctness fixes (do first)
1. **Commit-ordering bug**: `Commit.AuthoredAt` is only set by push/PR webhooks; upload-only
   commits (the norm for OIDC auto-provisioned repos) have null → RavenDB clusters them at one
   end of `OrderByDescending(AuthoredAt)` (`Commits_ByRepository`), so the repo page isn't
   newest-first and the Δ column (computed against "next row down") is wrong. Fix: stamp
   `FirstSeenAtUtc` at upsert in BOTH webhook and upload paths; index coalesced with AuthoredAt.
2. Zero delta renders blank: `@if (delta(i); as d)` — `0` is falsy (`repo.component.html`).
3. Badge markdown uses `location.origin` → dead URLs when copied from localhost. Serve
   `Coverage:BaseUrl` to the SPA (e.g. on the repo response) and build the snippet from it.
4. Stale "designed for extraction" comment in `ApiToken.cs` (that plan was cancelled — PRD §10).

### Missing product features (PRD promises, verified unbuilt)
5. **Upload-token management UI** — backend complete (`TokensController` create/list/revoke) but
   NOTHING calls `/api/tokens`; today tokens are minted by hand-POSTing. PRD §9.2's account admin
   tab. Include revoke (handler enforces revocation already) and repo-scoped tokens —
   `ApiToken.Scope="Repository"` is honored on upload but `TokensController.Create` hardcodes
   `"Account"`, making the scope unreachable.
6. Branch selector on the repo page (server + service already support `?branch=`; UI never sends it).
7. Home-page aggregates (repo count + aggregate coverage per account — PRD §9.1).
8. Manual "resync" of GitHub visibility (only the 5-min TTL exists — PRD §6.1).
9. Coverage-over-time: history endpoint (derivable from `Commits_ByRepository` + `Commit.Coverage`)
   + `bs-trend-chart` (with `goal` line) on the repo page; `bs-sparkline` in tables.
10. More parsers: **JaCoCo first** (validates the nullable-Hits design; was "M2.5", never happened),
    then Istanbul JSON, Clover, OpenCover, Go; opt-in ReportGenerator.Core fallback adapter.
11. PR comments + commit statuses/checks (needs checks:write + PR:write on the App).
12. Patch/diff coverage (inputs exist: `ParentSha` stored; note the action's wire field is
    `parentSha` — there is no separate baseSha field).
13. Fork-PR quarantine flow (today forks simply can't upload).

### Ops / deployment
14. **Image publish workflow** — compose pulls `ghcr.io/mintplayer/codecoverage:master` but no
    workflow builds/pushes it.
15. Traefik port ambiguity: Dockerfile exposes 8080+8081 with no
    `traefik.http.services…server.port` label — Traefik may pick 8081. Drop EXPOSE 8081 or label.
16. Compose healthchecks (`/health` exists; wire `healthcheck` + `depends_on: service_healthy`).
17. `.env.example`: document the `./github-app.pem` bind-mount.
18. CI dogfood never exercises OIDC (token-based, conditional on `vars.COVERAGE_URL`; no
    `id-token: write`) — add an OIDC leg once deployed.
19. Action versioning: tag `v1`, README consumer usage (`uses:` snippet, badge URL, token-vs-OIDC);
    remove the unused `check-dist` npm script.

### Performance / scale / hardening
20. Tree + hierarchy endpoints re-stream the whole build's FileCoverage per request (commit page
    fires both on load) — cache per build or materialize a TreeSummary at finalize.
21. File-view virtualization (upstream component also renders plain; 2000 rows measured fine —
    watch item for giant generated files).
22. Live refresh of in-flight builds (no polling/SSE; Pending sessions need manual reload).
23. OIDC auto-provisioning is unbounded (any public repo can create docs + 50MB attachments;
    rate limiter bounds rate, not cumulative storage) — quota/retention.
24. Badge shares the "uploads" rate-limit policy — a popular badge behind GitHub's camo proxy
    (few IPs) can throttle itself; split policies.
25. Per-branch badges (`?branch=` is documented nowhere anymore — the endpoint serves the
    default-branch `LatestCoverage` only; implement branch lookup if wanted).

### Testing
26. Integration tests via `MintPlayer.Spark.Testing` (embedded RavenDB; needs `RAVENDB_LICENSE`) —
    upload endpoint, auth handlers, finalization FIFO, browse API. Current suite = 20 pure-unit
    tests (parsers/merger/normalizer) only.
27. ClientApp declares `"test": "ng test"` but installs no test runner — add one or drop the script.

### UI upgrades (components exist upstream, adoption optional)
28. Folder list → `bs-datatable` tree mode (expandable rows + sortable coverage columns + lazy
    child fetch; https://bootstrap.mintplayer.com/enterprise/datatables) replacing the plain
    `bs-table` + breadcrumb drill-down — pairs naturally with the `[(rootId)]`-synced sunburst.

---

## Status (2026-08-10)

| Milestone | State |
|---|---|
| M0 Spark groundwork | ✅ Resolved upstream by [Spark#231](https://github.com/MintPlayer/MintPlayer.Spark/pull/231) (ApiTokens lib cancelled → app keeps `covt_`; see PRD §10) |
| M1 Scaffold · M2 Ingestion · M3 Action · M4 Browse UI · M6 Badges · M7 OIDC | ✅ Built, verified E2E, on `develop` |
| M5 File view | ✅ App side built (hand-rolled renderer); viewer component shipped upstream ([#402](https://github.com/MintPlayer/mintplayer-ng-bootstrap/pull/402)) — adoption is M10 |
| M8 Upgrade + diagram | ✅ Built (preview.42 + 22.14 upgrade, ApiToken credential scheme, popup login, sunburst + ring on commit page) |
| M10 Code-viewer adoption | 🔓 Unblocked — pin 22.15.0/2.12.0 + highlight.js, swap the file renderer |
| M9 Verified backlog | pending — audited 2026-08-12, list below is code-verified |

**Nothing remains upstream.** All three repos delivered (Spark#231, ng-bootstrap#401 + #402);
the only open upstream nit is cosmetic Sass `@import` deprecation noise. Two audit corrections to
older claims: upstream found `bsShellTopbar` needs no promotion (`<div slot="topbar">` works
directly — delete our directive in M10.3) and the `bs-progress-bar` host-class clobbering was
measured NOT real.

**As-built deviations from this plan:** the file browser is a plain `bs-table` with breadcrumb
drill-down — `bs-datatable` tree mode exists upstream (see
https://bootstrap.mintplayer.com/enterprise/datatables, "Tree mode — expandable rows") and
adopting it is M9.28; only the commit list has a static index (tree aggregation streams on the
fly — see M9.20); the badge endpoint has no `?branch=` (M9.25); typed webhook recipients were
deliberately skipped (M8 step 2.3 note).

## Sequencing notes

- M0 (Spark PR) and M3 (action) can proceed in parallel with M1/M2 once the token library's *interface* is agreed — the Coverage app can stub token auth briefly.
- M5.1 and M8.1 (ng-bootstrap PRs) are independent of the Coverage backend; they can start any time after M0's dependency bump, but sequence them M5 before M8 (code viewer is core UX; the diagram is delight).
- Test policy per global instructions: verify milestones by build/type-check + targeted fixture tests during development; full suites batched at the end of each milestone before its PR.

## PR map (one per repo)

| Repo | PR contents | Status |
|---|---|---|
| MintPlayer.Spark | M0 (queue-name fix, popup fix, ng-bootstrap bump, R4-H1, doc fixes; ApiTokens→client_credentials) | ✅ [#231](https://github.com/MintPlayer/MintPlayer.Spark/pull/231) |
| mintplayer-ng-bootstrap | Charts (hierarchy/trend/sparkline + public charts/core) | ✅ [#401](https://github.com/MintPlayer/mintplayer-ng-bootstrap/pull/401) |
| mintplayer-ng-bootstrap | Unified code-snippet viewer (M5's component half) | ✅ [#402](https://github.com/MintPlayer/mintplayer-ng-bootstrap/pull/402) (22.15.0/2.12.0) |
| Coverage | M1–M8 built incrementally on `develop`; next: M10 then M9 | 🔄 |
| coverage-action | Lives in this repo under `action/` (extract only for Marketplace) | ✅ |
