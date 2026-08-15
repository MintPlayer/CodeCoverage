# Adopting ng-spark's generic UI — PRD & plan

Status: **proposed**

Every ClientApp page today is hand-written: it fetches from the bespoke `/api` controllers and
renders its own `<bs-table>`, instead of reusing the generic renderer components shipped in
`@mintplayer/ng-spark`. This document records what the library actually ships, classifies every
page (drop entirely / recompose from library parts / keep custom), names the blockers that make
"just drop the page" impossible today, and lays out the phased plan — including the upstream Spark
work required first.

> Companion documents: [PRD.md](PRD.md) §2 (hard architectural rule: generic code goes upstream),
> [PLAN.md](PLAN.md), and in MintPlayer.Spark: `docs/PRD-CoverageHandoff.md` (which already
> observed that `sparkRoutes()` is "mounted but unreachable").
>
> Research basis: a three-agent investigation (2026-08-14) — ng-spark library API inventory,
> Coverage backend Spark-model/controller audit, and a MintPlayer.Spark documentation survey —
> plus a hand-read census of all five pages. Claims carry `file:line` evidence.

---

## 1. The finding

The app is Spark-native in plumbing only. `app.routes.ts:11,18` spreads `sparkAuthRoutes()` and
`sparkRoutes()`, `app.ts` mounts `<spark-retry-action-modal>`, and pages import only
`SparkLanguageService`, the `t`/`resolveTranslation` pipes, and `SparkAuthService`. **No page uses
`spark-query-list`, `spark-po-detail`, `spark-sub-query`, or `spark-po-form`. No page calls
`SparkService` for data.** All data flows through hand-written `[ApiController]`s
(`Coverage/Controllers/*.cs`) over raw `IAsyncDocumentSession`.

That is not an accident. `Program.cs:36-44` deliberately runs Spark authorization in **DenyAll**
mode (no `security.json` exists in the repo):

```csharp
// Deliberately DenyAll (no security.json): Spark's generic data endpoints are
// fully denied. All data access goes through our own /api controllers, which
// mirror the viewer's GitHub permissions. This also sidesteps the open
// R4-H1 finding (row-level auth missing on query-execute/stream endpoints.
```

So the generic `/query/:queryId` and `/po/:type/...` routes are routable but return denied for
every entity, and nothing links to them. **Any adoption of the generic UI is blocked on
authorization first, rendering second.** The rest of this document is structured around that fact.

---

## 2. What `@mintplayer/ng-spark` ships (verified against 22.0.8)

Full inventory in the research; the parts that matter for this plan, by composability tier:

### Tier A — drop-in anywhere (plain inputs, no route dependency)

| Component | Inputs | What it renders |
|---|---|---|
| `spark-sub-query` | `queryId`, `parentId`, `parentType` (all required) | `bs-card` + server-fetched sortable `bs-datatable` of a query filtered by parent; honors `renderMode`, custom cell renderers; first cell links to `/po/:type/:id`. No action bar/search/create. |
| `spark-po-form` | `entityType`, `[(formData)]`, `validationErrors`, `showButtons`, `isSaving`, `parentId?`, `parentType?` | Full create/edit form: tabs/groups/columnSpan, per-datatype editors, Reference/Lookup pickers, AsDetail inline/modal, drag-reorder, inline validation. Bridge with `nestedPoToDict`/`dictToNestedPo`. |
| `spark-reference-picker`, `spark-lookup-picker` | value/options | standalone pickers |
| 23 pipes (`attributeValue`, `referenceLinkRoute`, `asDetailColumns`, …) | — | per-cell/field formatting reusable from any hand-written template |

### Tier B — route-driven generic pages (already mounted via `sparkRoutes()`)

`spark-query-list`, `spark-po-detail`, `spark-po-create`, `spark-po-edit` read `:queryId`/`:type`/
`:id` from `ActivatedRoute` — **they take no id inputs**, so they can't be embedded in a bespoke
page. Customization: `showCustomActions`, `extraActionsTemplate`, and (detail only)
`extraContentTemplate` (receives `$implicit: PersistentObject` + `entityType`).
`spark-po-detail` auto-renders one `spark-sub-query` per entry in `EntityType.queries[]`.
`sparkRoutes(SparkRouteConfig)` can swap each `loadComponent` (shipped but undocumented upstream).

### Tier C — the extension seam: attribute renderers

`provideSparkAttributeRenderers([{ name, detailComponent, columnComponent, editComponent? }])`
plus `renderer`/`rendererOptions` on the attribute in the model JSON injects a custom cell/field
component into **all four generic hosts** (query list, detail, sub-query, form) via
`NgComponentOutlet`. This — not a hand-written page — is the intended way to get custom visuals
like a coverage bar into a generic grid.

### Not shipped (would stay ours or go upstream)

No standalone action bar, no breadcrumb-trail component, no nav-menu component, and — critically —
**no way to customize the row links** the grids emit: they hardcode
`['/po', entityType.alias || entityType.id, row.id]` (query-list, sub-query, and the
`referenceLinkRoute` pipe alike).

---

## 3. Page census

Routes from `Coverage/ClientApp/src/app/app.routes.ts:6-21`. All five pages follow the same
pattern: subscribe to route params, `await` a `BrowseService`/`AccountsService`/`TokensService`
call (plain `HttpClient` → `/api/...`), render manually.

| Page | Route | Classification | Spark-shaped parts rendered by hand |
|---|---|---|---|
| home | `/home` | **fully custom** | none — the accounts list is GitHub-installation data (`/api/me/accounts`: installed badge, repo count, aggregate coverage, reauth banner), not a Spark query |
| account | `/a/:login` | **partially generic** | Card 1 (`account.component.html:10-47`): a `<bs-table>` of the account's repositories = the `Account → GetRepositories` relation rendered by hand. Card 2 (upload tokens) is fully custom — `ApiToken` is deliberately not in the Spark model |
| repo | `/r/:owner/:repo` | **partially generic** | Commits card (`repo.component.html:79-121`): `<bs-table>` = `Repository → GetCommits` by hand, plus branch filter, Δ-vs-previous column, coverage bar. Badge management, trend chart, CI-examples tabs are custom |
| commit | `/r/:owner/:repo/c/:sha` | **partially generic** | Builds table (`commit.component.html:35-73`): `Commit → GetBuilds` with `Sessions` AsDetail by hand — structurally the closest match to `spark-sub-query`. Folder tree + sunburst + breadcrumb are custom (backed by `BuildTreeSummary`/`FileCoverage`, deliberately outside the model) |
| file | `/r/…/c/:sha/f` | **fully custom** | none — line-by-line `bs-code-snippet` viewer over `FileCoverage`, not modeled in Spark |

**Pages droppable entirely today: none.** And the question is subtler than "drop the page": the
generic equivalents (`/po/Account/:id`, `/query/GetRepositories`) are *already mounted* — they are
just denied, unlinked, and would render the wrong thing (see §4). The realistic wins are
(a) making the generic routes actually work as a secondary/admin surface, and (b) recomposing the
three hand-rolled query tables onto `spark-sub-query` + attribute renderers.

---

## 4. Why nothing can be dropped today — the gap list

1. **DenyAll authorization (the hard blocker — smaller than first thought).** Every Spark data
   component fetches through `SparkService` → `/spark/queries/...` / `/spark/po/...`, which deny
   everything (`Program.cs:36-44`). *Correction after the follow-up Spark investigation
   (2026-08-14):* the `Program.cs` comment cites "R4-H1", but per Spark's
   `docs/prd/PRD-SecurityAudit.md` that identifier is fabricated and the real findings (H-2/H-2a)
   were **resolved in Spark M5 (2026-08-09)** — row-level security ships in Spark core today as
   `IRowSecurity` + `DefaultPersistentObjectActions<T>.IsAllowedAsync(action, entity)`, enforced
   on every read path and on Edit/Delete. WebhooksDemo's `GitHubProjectActions` already implements
   almost exactly Coverage's rule (GitHub org membership). What genuinely remains upstream is
   tracked in [Spark#236](https://github.com/MintPlayer/MintPlayer.Spark/issues/236): a
   projection-path batching bug, expression pushdown (today's filter is post-materialization,
   O(collection) — a real problem for Coverage's commit/build volumes), create-side WITH CHECK,
   custom-action row gating, per-viewer attribute redaction, and per-row permissions for the UI.
2. **Anonymous read.** Public-repo browsing works logged-out (`BrowseController` has no
   `[Authorize]`). Expressible today: grant `Query`/`Read` to the `Everyone` group in
   `security.json` and let the row filter narrow to public rows (Spark#236 open question 2 asks
   to bless and document exactly this pattern).
3. **Secret leakage in the model.** `Repository.BadgeToken` is `isVisible: true`,
   `showedOn: "Query, PersistentObject"` (`App_Data/Model/Repository.json:133-146`). Harmless
   while DenyAll; the moment queries open, every viewer of a repo row sees its badge token.
   Same review needed for `Account.InstallationId`. Visibility must become per-viewer (the
   `canManage` notion), which today Spark's static model can't express — `IsVisible` is
   outbound-advisory only (the value still ships in JSON). Per-viewer attribute redaction is
   G4 of [Spark#236](https://github.com/MintPlayer/MintPlayer.Spark/issues/236).
4. **Row links are hardcoded to `/po/...`.** Coverage's canonical URLs are `/a/:login`,
   `/r/:owner/:repo`, `/r/…/c/:sha`. A `spark-sub-query` of repositories would link its rows to
   `/po/Repository/:id`. Either those generic routes become acceptable secondary destinations, or
   ng-spark needs a link-mapping seam (upstream).
5. **Empty `queries[]` on every entity type — and parent scoping doesn't exist upstream.**
   `App_Data/Model/*.json` declare `persistentObject.queries: []` everywhere, so no parent→child
   relation is modeled: a generic Account detail page would show no repositories subquery.
   Worse, the follow-up investigation found that for `Database.*` queries the server **validates
   `parentId`/`parentType` and then ignores them** (`Execute.cs:97-109` → `QueryExecutor.cs:36-44`)
   — only `Custom.*` sources can scope to a parent, so a `spark-sub-query` over a `Database.*`
   query would return the whole collection. Flagged in Spark#236 as a related finding needing its
   own upstream issue before Coverage's M4 recomposition can use model-declared relations.
   *Status after Spark PR #237 (2026-08-15): still unfixed, and the follow-up issue the PR plan
   promised was never filed — this is currently tracked nowhere upstream.* Interim option:
   declare the sub-queries as `Custom.*` sources reading `args.Parent`.
6. **Custom columns exceed the generic cell model.**
   - *Coverage bar* (`app-coverage-bar` over a `CoverageSummary` AsDetail): expressible today as a
     registered `columnComponent`/`detailComponent` attribute renderer — this one is pure win.
   - *Sparkline* (account page): data comes from a separate batched endpoint
     (`/api/browse/accounts/{login}/sparklines`), not from the row. Needs a server-computed
     attribute (index-stored recent-percentages array) + a column renderer, or stays custom.
   - *Δ vs previous commit* (repo page): cross-row computation (`repo.component.ts:235-242`);
     either an index-computed attribute on Commit or it stays custom.
   - *Branch filter* (repo page): a parameterized query filter — no generic UI exists for
     query parameters beyond search.
7. **Version skew.** Backend is `MintPlayer.Spark 10.0.0-preview.43`; ClientApp pins
   `ng-spark ^22.0.8` / `ng-spark-auth ^22.1.0`. Any upstream additions land in new previews of
   both — adoption milestones must ride the usual upgrade train.

---

## 5. Target architecture

- **Vanity pages stay.** `/a/:login`, `/r/...`, commit and file pages keep their routes, layout,
  and custom panels — but their query tables become `spark-sub-query` instances (or a thin
  wrapper), and coverage rendering becomes a registered attribute renderer used by *both* the
  custom pages and the generic ones.
- **Generic routes become real.** `/po/Account/:id`, `/po/Repository/:id`, `/query/GetAccounts`
  etc. go from denied-and-unlinked to a working, row-secured secondary surface (useful
  immediately as an admin/debug view, and as the free UI for any future entity — that's the point
  of the framework).
- **The model becomes honest.** Related queries declared, secret attributes hidden per-viewer,
  computed columns (sparkline data, Δ) pushed into the model/index where cheap.
- **`/api/browse` shrinks but does not disappear.** Tree/hierarchy/file/source endpoints (backed
  by `FileCoverage`/`BuildTreeSummary`), `/api/me`, tokens, badges stay custom — they are not
  query-shaped.

---

## 6. Plan

Legend: 🟩 MintPlayer.Spark PR · 🟦 Coverage repo. One PR per repo per milestone
([PLAN.md](PLAN.md) conventions).

### M1 — Complete row-level security in Spark 🟩 (✅ DELIVERED 2026-08-14, Spark PR #237)

**Goal:** superseded by the upstream PRD filed as
[Spark#236](https://github.com/MintPlayer/MintPlayer.Spark/issues/236) (2026-08-14), implemented
by [Spark#237](https://github.com/MintPlayer/MintPlayer.Spark/pull/237) (squash `e251208`,
closes #236). All six gaps shipped: batched projection reload (G0), `GetRowFilter` expression
pushdown with the derivation rule + projection fallback (G1), create-side WITH CHECK with
`SparkSystemContext` for module principals (G2), row-gated custom actions via `Submitted*` (G3),
`GetProtectedAttributesAsync` redaction incl. AsDetail read-side + top-level write shielding (G4),
and the per-row `can` block consumed by `spark-po-detail` (G5) — plus a security sweep binding
document ids to the authorized type. **Carriers: `MintPlayer.Spark 10.0.0-preview.44` /
`@mintplayer/ng-spark 22.0.9`** (preview.43 has none of it).

**Not delivered upstream (still open):** #236-M6 (Raven Skip/Take pushdown — perf only), and the
**`parentId` ignored for `Database.*` queries** finding — verified still live on merged master
and *no follow-up issue was filed* despite the PR plan saying there would be. That one blocks M4
below.

### M2 — Coverage adopts the security seam 🟦 (✅ BUILT 2026-08-15, on preview.45/Spark#240)

**Goal:** open `/spark` reads safely. The async-hook dependency
([Spark#239](https://github.com/MintPlayer/MintPlayer.Spark/issues/239), see
[spark-async-row-filter.md](spark-async-row-filter.md)) shipped as
[Spark#240](https://github.com/MintPlayer/MintPlayer.Spark/pull/240) (preview.45), so the rules
are written async-first against `GetRowFilterAsync`.

As built:

1. Pins on `10.0.0-preview.45` / `@mintplayer/ng-spark 22.0.9`. Breaking changes checked:
   no Spark custom actions (`Submitted*` rename inert), no lookup references
   (`Read/LookupReferences` requirement inert).
2. `App_Data/security.json`: `QueryRead` on Account/Repository/Commit/Build for `Everyone` —
   the row filters are the only gate behind that, per the guide's anonymous-read warning.
3. `Coverage/Services/SparkVisibility.cs` — per-request Task-memoized snapshots (owners via
   `IGitHubAccessService`, visible repo ids via one Raven query), because the framework memo
   is per-(type, action): the hook still runs 3× per detail read.
4. `Coverage/Actions/`: `RepositoryActions` (`!IsPrivate || owners.Contains(OwnerLogin)`
   pushdown + `BadgeToken` redaction + `Account` include), `AccountActions` (`InstallationId`
   redaction only — accounts are public), `CommitActions` (pushdown IN over visible repo ids +
   `Repository` include), `BuildActions` (per-row predicate parsing the commit-id shape
   `Commits/{repoGitHubId}/{sha}` — no owner fields to push down on; in-memory after the
   memoized repo-id query, plus `Commit` include). `Enumerable.Contains` used in expressions
   (translates to RQL `in` AND works compiled in-memory; Raven's `.In()` is query-only).
5. Writes stay denied at the type level, so the WITH CHECK path is unreachable and the
   machine-principal trap doesn't apply.
6. Related-query declarations (`EntityType.queries[]`) deliberately **moved to M4**: declaring
   them before parent-aware sources exist would make generic detail pages render sub-queries
   containing the whole (row-filtered but unscoped) collection, since `Database.*` queries
   drop `parentId` upstream.

**Exit criteria — VERIFIED LIVE (Playwright + wire, 2026-08-15):** anonymous
`/spark/queries/repositories/execute` returns only public rows (128, zero private) with
`BadgeToken` nulled + `isVisible: false`; detail reads pass the compiled row check;
`account-repositories?parentId=Accounts/48772716` returns exactly the account's 3 repos.
Two findings fixed during verification:
1. **RavenDB cannot translate `Contains` in these filters on .NET 10** — a `string[]` receiver
   binds to span-based `MemoryExtensions.Contains`, and even `List<string>.Contains` inside
   `!x || list.Contains(y)` throws `TypedParameterExpression`. Raven's `.In()` is the shape that
   both translates and (verified live) evaluates in-memory for the compiled single-row checks.
2. **The per-row `can` block overclaimed upstream** — computed from the row rule alone, never
   intersected with type-level rights, so anonymous viewers got `can: {edit, delete} = true` and
   Edit/Delete buttons on the generic detail page. Filed as
   [Spark#243](https://github.com/MintPlayer/MintPlayer.Spark/issues/243), **fixed upstream by
   [Spark#244](https://github.com/MintPlayer/MintPlayer.Spark/pull/244) (preview.46)** — the
   block now intersects type-level rights server-side. Coverage's interim `x => false`
   write-action guard was removed again with the preview.46 bump; the rules are back to a single
   visibility expression per type.

### M3 — Attribute renderers for coverage visuals 🟦 (✅ BUILT 2026-08-15 — 🟩 gap found and filed)

**As built:** `CoverageBarRendererComponent` (one class, column + detail slots) registered as
`coverage-bar` in `app.config.ts`; `renderer: "coverage-bar"` declared on the three
`CoverageSummary` AsDetail attributes (`Repository.LatestCoverage`, `Commit.Coverage`,
`Build.Coverage`). ⚠️ The upstream gap this milestone anticipated materialized: **renderers on
AsDetail attributes receive `undefined`** (`EntityMapper.cs:276` nulls the flat value; every
ng-spark host passes only `itemAttr?.value`) — filed as
[Spark#241](https://github.com/MintPlayer/MintPlayer.Spark/issues/241) proposing a value fallback
to the nested PO. The renderer already handles that shape, so Coverage lights up with a package
bump and zero code changes when #241 ships. Until then generic hosts show the bar's empty state
(the column was blank before, too). The `parentId` scoping bug was also finally filed upstream as
[Spark#242](https://github.com/MintPlayer/MintPlayer.Spark/issues/242).

Original goal:

**Goal:** one `coverage-bar` renderer (column + detail) registered via
`provideSparkAttributeRenderers`, driven by `renderer: "coverage-bar"` on the `LatestCoverage` /
`Coverage` AsDetail attributes; reused by generic pages and (M4) the custom pages.

**Exit criteria:** the generic repository list shows the same coverage bars as `/a/:login`.

### M4 — Recompose the hand-rolled tables 🟦 (✅ BUILT 2026-08-15)

**As built:** parent scoping via `Custom.*` sources on the Actions classes
(`RepositoryActions.Account_Repositories`, `BuildActions.Commit_Builds` — the Spark#242
workaround; the framework still applies row filters, sorting, and includes on top). Declared as
model queries with aliases `account-repositories` / `commit-builds`, plus `EntityType.queries[]`
on Account/Commit so the generic detail pages auto-render the same sub-queries. The account
page's repositories card and the commit page's builds table are now `<spark-sub-query>`
(`account.component.html`, `commit.component.html`); per-session parse detail moved to the
generic Build detail page. The sparkline survives as the `coverage-sparkline` renderer bound to
`Repository.FullName` (label "Trend") — it works today (scalar value), while `coverage-bar` waits
on Spark#241. `showedOn` trimmed across the model so the generic grids show curated columns
(secrets/ids/plumbing are detail-only). New `/api/browse/accounts/{login}` returns the account
document id (sub-query `parentId`); the commit payload gained `id` for the same reason (its
`builds` array is now unused by the SPA — trim in a follow-up). Test sweep: 54/54 passed.

Original goal (scope fixed by D2–D4):

1. Account page card 1 → `<spark-sub-query queryId="GetRepositories" [parentId]=... parentType="Account" />`,
   with the sparkline preserved as a `FullName` column renderer (D3). Parent scoping needs a
   parent-aware query source until the upstream `Database.*` parentId bug is fixed (§4.5).
2. Commit page builds table → `GetBuilds` sub-query (Sessions renders as the AsDetail sub-table),
   same parent-scoping note.
3. Repo page commits table: **out of scope** (D4) — keeps the hand-written table.

**Exit criteria:** the account repositories card and the commit builds table are rendered by
`spark-sub-query`; `BrowseController` endpoints that became redundant are deleted (endpoints
still consumed elsewhere — e.g. the repos list feeding the token-scope dropdown — stay).

### ~~M5 (optional) — Row-link seam in ng-spark 🟩~~ (DROPPED per D2, 2026-08-15)

The user accepted `/po/...` Spark routes as the grid link targets, so no link seam is needed.
(An implementation-ready design was produced during the investigation — `provideSparkLinks` +
`SparkLinkService` over a `SPARK_LINK_RESOLVERS` token, covering the three grid anchors,
`referenceLinkRoute`, and post-save navigation — and can be revived if that decision ever flips.)

### M6 — Grid parity with the master-branch cards 🟦 (🟩 row-context seam filed upstream)

**Finding (user, 2026-08-15, vs coverage.mintplayer.com):** replacing the hand-written cards
changed their shape. Master's account card shows **Repository (name + inline "private" badge) ·
Coverage (bar) · Trend (sparkline) · Latest commit (7-char sha link)**; master's builds card shows
**Run (`runId.attempt`) · Status (+ finalize reason) · Sessions (job + parse badges) · Coverage ·
Created**. The generic grids showed raw schema columns instead. The cards/grids/columns must match
master; the `/po` links inside them are fine (D2).

**Coverage-side (now):**
1. Model JSON: relabel + reorder + re-trim `showedOn` so the Query columns are exactly master's
   sets — Repository card: `Name`("Repository"), `LatestCoverage`("Coverage"), `FullName`("Trend"),
   `LatestCoverageSha`("Latest commit"); Build card: `CiRunId`("Run"), `Status`, `Sessions`,
   `Coverage`, `CreatedAtUtc`("Created"). `OwnerLogin`/`IsPrivate`/`WorkflowName`/
   `FinalizedAtUtc`/`FinalizeReason` become detail-only.
2. New value-only renderers, registered alongside the existing two: `short-sha` (7-char monospace,
   on `LatestCoverageSha`) and `build-sessions` (on the `Sessions` AsDetail array — renders the
   per-session job/parse badges once the value arrives, "—" until then).
3. Coverage bars and Sessions cells light up when
   [Spark#241](https://github.com/MintPlayer/MintPlayer.Spark/issues/241) ships (AsDetail
   renderer value).

**Upstream (filed):** the row-context-for-renderers seam — optional `item` input on the renderer
contracts, passed only when declared via a `reflectComponentType` filter (which also fixes a
latent upstream bug: a renderer omitting any of the current inputs throws at `NgComponentOutlet`
binding time). Unlocks the remaining master-parity cells: the inline "private" badge next to the
name, the `runId.attempt` composite, and a linkable latest-commit cell.

**Exit criteria:** the account and commit cards show master's exact column sets/labels/orders;
short-sha renders; the remaining cells upgrade automatically as the upstream pieces ship.

**✅ COMPLETE (2026-08-15):** [Spark#250](https://github.com/MintPlayer/MintPlayer.Spark/pull/250)
shipped #241 + #245 as `@mintplayer/ng-spark 22.0.11` (implemented from the PRD posted on #241).
Coverage adopted it: `rendererValue`/`item` verified live — the generic commit detail renders the
Coverage bar (50.0% on the seeded JObject commit), the auto-rendered Builds sub-query shows
**Run (`302.1`, the computed property) | Status | Sessions ("… Parsed" badges via the
build-sessions renderer) | Coverage (bars) | Created**, and two new `item`-consuming renderers
complete the parity cells: `repo-name` (inline "private" badge next to the name) and the upgraded
`short-sha` (links to the vanity commit page derived from the row's FullName). Every open
upstream issue from this adoption is now closed (#236→#237, #239→#240, #243→#244, #241+#245→#250);
only #242 (Database.* parentId — worked around with Custom.* sources) remains open.

### M7 — Rich detail-page parity on the generic surface 🟦 (✅ BUILT 2026-08-15)

**Finding (user):** `/po/repository/...` rendered only the attribute card, while master's
`/r/{owner}/{name}` has the badge, the interactive coverage-over-time graph, commits, and setup
instructions. Requirement: both URLs render the same panels, staying on the **generic Spark
pages** customized only through framework seams.

**As built:** the repo page's panels were extracted into shared standalone components
(`RepoTrendPanelComponent`, `RepoSetupPanelComponent`, new `RepoBadgePanelComponent`) — the vanity
`/r` page renders identically through them — and the app now overrides
`sparkRoutes({ poDetail })` with a thin `PoDetailPageComponent` that renders the stock
`<spark-po-detail>` plus, via its `extraContentTemplate` slot, the three panels when the entity
type is Repository (owner/name derived from the PO's FullName). A parent-scoped
`Custom.Repository_Commits` query declared as `Repository.queries: ["repository-commits"]` gives
the generic detail its Commits card automatically. Verified live on `/po/repository/…` (seeded
acme/demo): attribute card with bar/sparkline/sha-link renderers → Commits sub-query → Coverage
badge card → interactive Coverage-over-time chart → Set up coverage uploads tabs.

**Extended same day — Commit parity + polish (user goal: clicking a repo from the account page
must land on the full master-like content, URL free to differ):**
- The commit page's Files card (sunburst + drill-down folder list) extracted into shared
  `CommitFilesPanelComponent`; the vanity `/r/…/c/:sha` page uses it unchanged, and the generic
  `/po/commit/…` renders it via a `CommitFilesExtrasComponent` that resolves owner/name by
  **loading the referenced Repository PO** (deliberately not the reference breadcrumb — see below).
- Commits grids: message moved out of the cell into a `title` tooltip on the sha link — on the
  vanity repo page's table and, generically, via `rendererOptions: { "titleAttribute": "Message" }`
  on `Commit.Sha`'s `short-sha` renderer (verified live: each sha cell carries its message).
  Commit's Query columns are now master's set: Commit (sha + tooltip) | Branch | Coverage | Date.
- 🐛 Found upstream while wiring this:
  [Spark#251](https://github.com/MintPlayer/MintPlayer.Spark/issues/251) — a Reference
  attribute's resolved breadcrumb can name the wrong document (`Repositories/999001` →
  "JObject", a repo that doesn't even exist, while the doc's own breadcrumb is "acme/demo").
  Coverage sidesteps it by loading the referenced PO.

**Possible future upstream refinement (not filed):** a registered per-entity-type *detail panel*
seam in ng-spark (`provideSparkDetailPanels([{ type, component }])`, mirroring the attribute
renderers) would make even the thin `poDetail` wrapper unnecessary — worth an issue if more panel
types accumulate.

### Sequencing

M1 → M2 strictly ordered; M3 can start in parallel with M2 (renderer registration is client-only);
M4 after M2+M3; M5 independent, pulled forward only if D2 demands it. Tests batched per repo at
the end of each milestone per the global test policy.

---

## 7. Explicitly rejected

- **Client-side data-source abstraction in ng-spark** (letting `spark-sub-query` fetch from
  `/api/browse` instead of `/spark`): duplicates the query pipeline client-side, leaves DenyAll
  unsolved for the generic pages, and violates "different layer, different abstraction". The
  server-side security seam is the deep fix.
- **Adopting `/po/...` as the canonical URLs**: breaks the shareable vanity URLs
  (`coverage.mintplayer.com/a/PieterjanDeClippel`) and README badge links for zero user benefit.
- **Modeling `FileCoverage`/`BuildTreeSummary`/`ApiToken` into Spark** just to genericize the
  file/tree/token views: these are deliberately outside the model (per-entity XML docs); their
  UIs are genuinely bespoke (code viewer, sunburst, token-reveal flow).

## 8. Decisions

| # | Decision | Resolution (implementation defaults, 2026-08-15 — each cheap to reverse) |
|---|---|---|
| D1 | ~~Shape of the upstream row-security hook~~ | Moved to [Spark#236](https://github.com/MintPlayer/MintPlayer.Spark/issues/236), shipped in PR #237: both hooks, with derivation (expression is source of truth when present; predicate refines) |
| D2 | Row links from generic grids | **RESOLVED by the user (2026-08-15): `/po/...` Spark routes are accepted as the grid link targets — permanently, not as an interim.** The link-resolver seam (old M5) is dropped; a full implementation-ready design for it exists in the investigation record should it ever be wanted. What must match master instead is the **visual grid parity** (M6) |
| D3 | Sparkline + Δ columns | **Sparkline survives** via a column renderer on `FullName` that batch-fetches `/api/browse/accounts/{login}/sparklines` (renderers are Angular components — they can inject services). **Δ stays hand-written** (cross-row computation, see D4) |
| D4 | Branch filter on commits | **Keep hand-written** — the repo page's commits table is out of M4 scope (branch filter + Δ have no generic home) |
| D5 | Generic surface user-facing or admin-only? | **User-facing**: `spark-sub-query` grids become part of the product pages; `/po`/`/query` routes are a legitimate secondary surface now that rows are secured |
