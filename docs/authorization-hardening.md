# PRD + Plan — Authorization hardening: per-repository entitlement, anonymous surface, and the accounts grid

**Status: PROPOSED 2026-08-20. Not started.**

**Owner decisions taken 2026-08-20, all folded in below:**

- **SP4** → one `Admin` level = `permissions.admin` (no preference expressed; simplest safe default).
- **SP5** → entitlement is derived from GitHub but **persisted**, not re-fetched per request (D1–D3),
  which makes account-scoped token rights a free local computation.
- **SP10** → `Organization: Members: Read` plus four webhook subscriptions — **Member**,
  **Organization**, **Team**, **Team add** — keep the stored lists current (D3 mechanism 3), in two
  invalidation shapes.
- **D5** → keep anonymous public-repo read. Public coverage stays world-readable; M2 tightens
  everything around it.
- **D3a** → **accepted**: keep one live check on the private-source path.
- **OIDC auto-provisioning** → keep it, **add a quota** (M7).
- **Badge on a private flip** → render `unknown` and surface a warning to the owner (M1.8).
- **Breaking changes are allowed** — the app has no real users yet. This is load-bearing for the plan,
  not a footnote: it is why M0 exists at all, why no compatibility shims or dual-read paths appear
  anywhere below, and why data migrations can rebuild rather than preserve.

- **SP12** → `Membership` subscribed too, so team-mediated access is covered from both sides and the
  **TTL is set to 60 min** (a pure backstop, not the primary bound).
- **C1** → 🟩 **handed off to Spark**, which will add a feature to disable the local-login endpoints
  *and pages*. Adopt when it ships; app-local stopgap only if needed sooner (M5.1).
- **SP13** → resolved from the `Octokit.Webhooks` types: every relevant payload carries a numeric
  `Organization.Id` / `Member.Id`, so both invalidation shapes have a key (D3).
- **SP1** → resolved from the REST docs: the bulk endpoint returns per-repo `permissions` by design, and
  GitHub Apps use permissions rather than OAuth scopes, so the missing `Scope` is a non-issue.
- **SP11** → resolved from the referenced assemblies: `AppPermissions.Members` exists; read it off the
  `installation` event only (every other event carries `InstallationLite`, which has no permissions).
- **SP3** → **not settleable from docs**, so the design was changed to not depend on it.
- **`RAVENDB_LICENSE`** → exists as an org-wide secret, so this plan has **no external prerequisite**
  and `PLAN.md` M9.26's blocker is stale.

**M4 revised 2026-08-21** after a three-agent investigation (Spark's index/query pipeline; the
Vidyano+CronosCore reference apps at `C:\Repos\Fleet` and `C:\Repos\Insurance`; a design review of
the proposed multi-map index). The owner's index-based design was **assessed and rejected on three
independent grounds** — it cannot express the `!IsPrivate` half of the visibility rule, Spark's row
security drops projected rows whose id names no document, and `LoadDocument<Account>` would collide
with M2's five-minute publicity reconciliation. Two premises also turned out false: Spark pages **in
memory on every path**, so no index buys paging, and `[IgnoreForIndex]` is not the column-leak fix.
The actual sorting win is one keyword (`isAsync`). See §6 M4 for the evidence and §6a for the four
upstream asks that fell out of it.

**No spike now blocks implementation.** Everything remaining is a first-run observation (SP1's
selected-repositories edge, SP3's owner check, SP13's `Repository` nullability) that the code should log
rather than a question that must be answered before writing it.

Answers three questions raised by the owner on 2026-08-20:

1. Unauthenticated users must not see pages they aren't supposed to; users must not see
   organization/repository pages of GitHub accounts they aren't a member of. And: *what should happen
   when a second member of an already-registered org signs in?*
2. The homepage "Your accounts" card wraps unpleasantly on mobile — make it a Spark CustomQuery
   rendering a datatable (preferred) or a hand-rolled datatable.
3. Every API/Spark endpoint must be protected so unauthorized callers cannot read other people's data.

> **Research basis.** A five-agent investigation (2026-08-20) covering: the Coverage authorization
> state, the full endpoint inventory, the GitHub identity/membership model, Spark's authorization
> primitives at `10.0.0-preview.57`, and the homepage card. File:line references are to `C:\Repos\Coverage`
> unless prefixed `Spark@` (= `C:\Repos\MintPlayer.Spark` @ `5464aa6`, which is preview.57 + a README commit).
> Findings labelled **[verified]** were read in the code; **[unverified]** means it could not be
> confirmed from source and needs a spike.

---

## 1. The structural diagnosis

Everything below follows from one fact: **the app never asks GitHub about the specific repository a
caller is trying to read.** Entitlement is computed from two proxies, and both are wrong in a
different direction.

| Proxy | What it is | How it fails |
|---|---|---|
| **`Repository.IsPrivate`** | a *cached mirror* of GitHub visibility, written only by the webhook path (`GitHubEventsRecipient.cs:251`) and hardcoded `false` by OIDC auto-provisioning (`UploadsController.cs:446`) | goes stale, and for OIDC-provisioned repos is stale **by construction** — those repos have no App installed, so the only writer that ever corrects it never fires |
| **the allowed-**owner** list** | `GET /user/installations` → the set of *account logins* the caller can reach, cached 5 min (`GitHubAccessService.cs:80-84`) | **owner-granular**: reaching an installation grants every repo of that owner. GitHub's own semantics are per-repo |

That single boolean is read by **seven** gates (`BadgeController.cs:70`, `BrowseController.cs:59,146,449`,
`GitHubContentService.cs:51`, `GitHubDiffService.cs:57`, `RepositoryVisibility.cs:36,43`), and the owner
list is read by **thirteen** call sites across `Actions/` and `Controllers/`. Neither is authoritative.

### 1.1 The sharpest consequence: source-code disclosure

Raised by the owner with a live URL. `GET /api/browse/repos/{owner}/{name}/commits/{sha}/file?path=`
(`BrowseController.cs:379-409`) returns **file source fetched live from GitHub**, and
`GitHubContentService.GetFileContentAsync` fetches it with the **App installation token**
(`GitHubContentService.cs:37-49`), which holds `contents: read` on every installed repository.

The gate in front of it is `ResolveVisibleRepository` (`BrowseController.cs:439-452`):

```csharp
if (!repository.IsPrivate) return repository;                     // ← no auth at all
var owners = await gitHubAccess.GetAllowedOwnersAsync(ct);
return RepositoryVisibility.IsVisible(repository, owners) ? repository : null;   // ← owner-granular
```

Two disclosure paths, both **[verified]**:

- **Authenticated, cross-repo.** Anyone whose `/user/installations` reaches the `acme` installation —
  including an **outside collaborator on a single public `acme` repo** — reads the full source of
  *every private `acme` repo that has coverage data*. The app lends out its installation privilege on
  the strength of a check that never asked GitHub about that repo. This is a confused deputy.
- **Anonymous.** An OIDC-provisioned repo (`IsPrivate = false`, no installation, nothing ever corrects
  it) that is later made private on GitHub keeps serving coverage, file paths and the badge to the
  public internet forever. Source itself stops flowing only because there is no installation token to
  fetch it with — coverage data and the full file tree still do.

`path` is constrained to files that have coverage (`FileCoverage.DocumentId(buildId, path)` must
resolve, `BrowseController.cs:389-391`), so this is not an arbitrary file proxy — but for a repo with
coverage that is most of the source tree.

### 1.2 The second consequence: manage rights equal read rights

`IsOwnerAllowedAsync(ownerLogin)` is the *only* gate on minting upload tokens (`TokensController.cs:37`),
listing them (`:81`), revoking them (`:108`), rotating the badge token and rewriting the gate policy
(`RepoSettingsController.cs:78`). It is the same owner-granular check used for reads. So **any** org
member who can reach the installation — read-only on one repo, or an outside collaborator — can mint
an account-scoped upload credential covering the whole org and revoke everyone else's.

Already filed as `PLAN.md` M9.29 and roadmap T2.3, both proposing a `GET /user/memberships/orgs/{org}`
role check. **This plan supersedes that approach** — see D2.

---

## 2. Answering question 1: two users, one organization

**The existing decision is correct and does not change.** `PRD.md` §6.3 already settled it: *GitHub is
the authority; no join workflow.* The code implements it correctly — and this was traced end-to-end,
not assumed:

`GitHubAccessService.BackfillInstallationIdsAsync:189-208` keys the document on GitHub's numeric
account id (`Account.DocumentId(gitHubId)`, `Account.cs:27`), so user B lands on the *same* document A
created. `Login`/`Type`/`AvatarUrl` are written only inside the `if (account is null)` branch, so B
never overwrites A's data; only `InstallationId` is written unconditionally, idempotently, to the same
value. The "own account absent ⇒ clear `InstallationId`" branch (`:210-222`) is deliberately guarded to
the caller's *own* login precisely so B's narrower visibility cannot clear A's org state. **[verified]**

So: **no duplicate org, no join request, no approval queue, no overwrite.** B simply sees it, because
GitHub says B can. That is the whole answer, and it needs no code change.

`Account` deliberately has no member list — `PRD.md:92` carries the comment *"no Admins list — GitHub is
the authority (§6.3), nothing cached"*. Keep it that way. Persisting membership would be building the
ACL layer the architecture explicitly bet against (roadmap §6 rejects it in those words), and its
failure mode is unbounded stale access: a user removed from an org keeps access until they next sign in.

**What must change is not the collision behaviour but the granularity.** "B is a member of acme" is
being used to answer two questions it cannot answer:

- *may B read repo X of acme?* — GitHub's answer is per-repo, ours is per-owner
- *may B administer acme's upload tokens?* — GitHub's answer is a role, ours is mere visibility

Both are fixed by the same single call. See §3.

---

## 3. Target design — derive from GitHub, persist locally, invalidate on webhooks

### D1 — Entitlement is fetched from GitHub **in bulk, at sign-in**, and **persisted**

*(Owner's direction, 2026-08-20: derive it from GitHub, but store it — don't bombard the API.)*

The source of truth stays GitHub. What changes is that we ask **once per session, for everything at
once**, instead of once per repository on demand:

| Call | When | Yields |
|---|---|---|
| `GET /user/installations` | sign-in, resync, TTL expiry | the installations this user can reach (already used today) |
| `GET /user/installations/{id}/repositories` | same, once per installation (paginated) | **every repo in that installation the user has explicit access to, each with a `permissions: { admin, push, pull }` hash** |

That second call is the one this plan originally rejected — correctly, for a *per-request* design,
where a user in 5 orgs would spend 72–360 calls/hr against their **shared** 5,000/hr user budget. As a
**once-per-sign-in bulk snapshot** it is exactly the right call: it is the only endpoint that returns
the complete per-repo entitlement set with permissions in one pass, and the cost is `1 + N`
installations per *session*, not per request.

Rejected alternative, and why: `GET /repos/{owner}/{repo}` per repository. Authoritative at the moment
of serving, but it is a call per (user, repo) — the "bombardment" shape the owner ruled out.
Also rejected: `GET /user/memberships/orgs/{org}` role=admin (the approach `PLAN.md` M9.29 and roadmap
T2.3 proposed). It needs `read:org`, answers per-*org* rather than per-repo, and is redundant once the
`permissions` hash is persisted. Note also that `GET /user/orgs` is useless here — it returns an
**empty list** for fine-grained tokens, i.e. for a GitHub App user token. **[verified against docs.github.com]**

### D2 — Two entitlement levels

```
None   → 404. Not visible.
Read   → may browse coverage, tree, source, badge.          (permissions.pull)
Admin  → may mint/revoke upload tokens, rotate the badge     (permissions.admin)
         token, rewrite the gate policy.
```

*(SP4 resolved — owner had no preference, so one level, not two.)* `Admin` is `permissions.admin`
rather than `push`: fewer concepts, and it errs toward refusing rather than granting. If it turns out
to lock out maintainers who wire CI without admin rights, relaxing **repo-scoped** token minting to
`push` is a one-line change; the reverse mistake is a disclosure.

Everything currently behind `IsOwnerAllowedAsync` splits along this line: reads → `Read`, the four
write surfaces in `TokensController`/`RepoSettingsController` → `Admin`.

**SP5 resolved, and it comes out free.** An account-scoped `covt_` token spans every repo of an owner,
so it requires `Admin` on **every repo of that owner in the persisted set**. Because the set is already
local, that is a `.All(...)` over an in-memory list — **zero** extra GitHub calls, and no
`memberships/orgs` call after all. Consequence to accept: a member with admin on 2 of an org's 5 repos
can mint repo-scoped tokens for those 2 but not an account-scoped one. That is the correct answer.

### D3 — The store: one document per user, plus a per-account epoch

**`UserAccess/{userId}`** — a single document holding that user's whole entitlement snapshot:

```
Owners            : string[]                       // as today, for list queries
Repositories      : [{ RepoGitHubId, Level }]      // Read | Admin, per repo
BuiltAtUtc        : DateTime
BuiltAgainstEpochs: { AccountId → int }            // see below
```

Serving costs **one RavenDB point-load by a natural id** — query-free, no GitHub call, nothing in the
hot path. This is strictly cheaper than today's `IMemoryCache` + `GET /user/installations`, and it
**fixes the multi-replica defect for free**: roadmap §5 records that the current per-process cache
means `resync` clears only the replica that served the request. A persisted document has no such
problem, so an item listed as deferred there is closed by this design rather than deepened by it.

Refreshed on: sign-in, explicit `POST /api/me/accounts/resync`, and TTL expiry (lazily, on the request
that notices).

**Invalidation without fan-out writes.** The danger of persisting entitlement is *stale access* — and
that is precisely why roadmap §6 rejected persisted membership. Two mechanisms bound it:

1. **A TTL that is never infinite.** 30–60 min. This is the backstop and it is not optional: it is the
   only thing that still works when a webhook is dropped, and dropped webhooks are a known hazard in
   this system (a Failed message never retries, and a 200 to GitHub does not mean processed).
2. **`Account.AccessEpoch`, an int bumped by the `installation` / `installation_repositories` webhooks.**
   A `UserAccess` document records the epoch it was built against per account; a mismatch means stale,
   and the document refreshes on next use. This gets near-immediate invalidation on installation
   changes **without** having to know which users are affected — no fan-out write, one integer per
   account.

3. **Membership webhooks** *(owner's suggestion, 2026-08-20 — the mechanism that makes this design
   properly safe rather than merely acceptable).* The owner confirmed in the App's own settings that
   granting **`Organization: Members: Read`** makes exactly two relevant event subscriptions available:

**Five** events are subscribed (owner, 2026-08-20), and they split into **two invalidation shapes** —
the distinction is not cosmetic, it decides the implementation:

   | Event (as labelled in the App UI) | Covers | Payload names a user? | Invalidation |
   |---|---|---|---|
   | **Member** — "Collaborator added to, removed from, or **has changed permissions for** a repository" | the outside-collaborator case from §1.1, **and** admin→read demotions | ✅ yes | **targeted**: delete that one user's `UserAccess` |
   | **Organization** — "deleted, renamed, member invited, member added, or member removed" | org joins and leaves; `renamed` also feeds M0 | ✅ yes | **targeted**: same |
   | **Membership** — user added to / removed from a **team** | the user↔team half of team-mediated access | ✅ yes | **targeted**: same |
   | **Team** — created, deleted, edited, added to / removed from a repository | the team↔repo half | ❌ **no** — team + org + repo only | **coarse**: bump `Account.AccessEpoch` |
   | **Team add** — a repository is added to a team | same | ❌ no | **coarse**: same |

   That "changed permissions" clause on **Member** is a real bonus: a demotion from admin to read revokes
   *management* rights near-immediately, not just access.

   **Why the team events must be coarse.** Their payloads carry the team, not its membership. Resolving
   them to users would mean `GET /teams/{id}/members` per event — precisely the API bombardment ruled
   out, and a call that grows with team size. The epoch bump avoids it entirely: one integer write on the
   `Account`, and every `UserAccess` document built against an older epoch is stale on next use. The
   mechanism added above for installation changes turns out to be exactly the right shape here, so team
   coverage costs **zero** extra API calls and zero fan-out writes.

   **Team-mediated access is now covered from both sides**, which is what closes the last real gap:
   `Membership` catches the user moving between teams (targeted, since it names them), `Team`/`Team add`
   catch the team's repo access changing (coarse, via the epoch). Neither needs a member lookup.

   **Payload shapes — verified, not assumed** (SP13, resolved by reflecting over `Octokit.Webhooks`
   4.1.2, the package Spark already references; types live in `Octokit.Webhooks.Events`):

   | CLR type | Carries | Consequence |
   |---|---|---|
   | `MembershipEvent` (+ `Membership.MembershipAddedEvent` / `MembershipRemovedEvent`) | `Member: User`, `Team: Team`, `Organization: Organization`, `Scope` | ✅ **targeted** — `Member.Id` is an `Int64` GitHub user id |
   | `MemberEvent` | `Member: User`, `Repository`, `Organization` | ✅ **targeted** — same |
   | `TeamEvent` (+ `Team.TeamAddedToRepositoryEvent`) | `Team`, **`Repository`**, `Organization` | ✅ **epoch** — `Organization.Id` is the bump target |
   | `TeamAddEvent` | `Team`, `Repository`, `Organization` | ✅ **epoch** — same |

   `Organization`, `User` and `Team` each expose a `Login`/`Name`/`Slug` **and** an `Int64 Id`, so every
   invalidation keys on a **numeric id, never a mutable string** — which is exactly what M0 re-keys the
   rest of the app onto. Two consequences worth noting:

   - **`Member.Id` is a GitHub user id, not our local `SparkUser` id**, so a targeted invalidation needs
     a lookup from one to the other. M0 makes that a point-load instead of a login-string query — another
     reason it sequences first.
   - **The team events also carry `Repository`.** If it is non-null on `added_to_repository` /
     `removed_from_repository` (worth confirming — it is typed non-nullable but GitHub omits fields for
     some actions), a narrower per-repository invalidation is possible instead of the account-wide epoch.
     Not worth building first: the epoch is correct, just coarser, and over-invalidating costs one
     refetch. Recorded so the option isn't lost.

**The permission cost, and the trap that comes with it.** These events require **read access to the
"Members" organization permission** on the App — a *new* permission on an existing App. GitHub does not
apply new permissions retroactively: **every existing installation must click-accept, and until it
does, no membership events arrive for that org and the app is silently back to TTL-only.** That silent
partial rollout is the trap, and it has bitten this project before in the same shape (roadmap §4 records
the identical hazard for a different permission). Two consequences for the design:

- **The TTL stays, and stays mandatory.** It is not superseded by the webhooks; it is the floor that
  still holds for un-migrated installations, dropped messages (a Failed message never retries here) and
  team-indirect changes. With webhooks accepted, 30–60 min is comfortable; without them it is the only
  bound there is.
- **Make the gap visible rather than silent.** `installation.new_permissions_accepted` is *already*
  handled (`GitHubEventsRecipient.cs:66-72`), so record per account whether the Members permission is
  granted, and shorten that account's TTL when it is not. A degraded account should be observable, not
  inferred — the roadmap's recurring lesson is that a silent degradation reads exactly like a working
  system.

With all three mechanisms, the residual stale-access window is **near-zero for every access path we can
name** — direct org membership, repo-collaborator changes including permission demotions, user↔team
membership, team↔repository access, and installation changes. Exactly two cases fall back to the TTL:
an installation whose org has not accepted `Members: Read`, and a dropped webhook message.

**That is why the TTL can be set at the comfortable end — 60 min.** It is now a pure backstop rather
than the primary bound on a common operation, which is the whole difference the webhook subscriptions
bought. It must still exist: dropped messages are a known hazard here (a Failed message never retries,
and a 200 to GitHub does not mean processed), and without a TTL a dropped invalidation is permanent.

**A webhook invalidates; it never patches.** The goal is keeping the stored lists current, and there are
two ways to do that — only one of them is safe:

- ✅ **Invalidate, then rebuild from GitHub.** The recipient deletes the affected `UserAccess`
  document; the next request rebuilds it from `GET /user/installations/{id}/repositories`. GitHub stays
  the sole writer of truth, and a missed, duplicated or out-of-order event degrades to the TTL — which
  is the *designed* fallback, not a corruption.
- ❌ **Patch the stored list from the payload.** Tempting (no refetch) but it makes the webhook an
  authoritative writer. A dropped event — and this system has a known hazard there, where a Failed
  message never retries and a 200 to GitHub does not mean processed — then leaves the list *silently
  wrong indefinitely*, with no TTL to catch it, because the document looks freshly written. It also
  means reimplementing GitHub's permission resolution (teams, base role, org owners) locally, which is
  the ACL we are explicitly not building.

Cost of the safe option: one bulk refetch per affected user per change, on their next request. That is
the right price.

**Public repos are answered separately and need no user context at all.** Keep a repo-level flag,
shared by every viewer: `Repository.IsPrivate` + a new **`Repository.VisibilityCheckedAtUtc`**. When a
repo believed public has a stale stamp, re-verify before serving. This is what closes A2, and it is the
only way a *cached* public flag can be safe — the flag stops being a permanent assertion and becomes a
lease. Anonymous browsing of public repos therefore stays at **zero** GitHub calls and zero
`UserAccess` loads, exactly as today.

**Listing vs serving.** The owner list survives inside `UserAccess.Owners` as the cheap first pass for
*list* queries (`Account_Repositories`, the home grid), because a list cannot afford a per-row
decision; `Repositories` is the per-repo authority for *serving* a specific document. Lists may
over-include; serving may not. A row that appears in a list but 404s on click is acceptable and rare;
the reverse is a disclosure.

### D3a — One live check retained, for the highest-value path *(ACCEPTED 2026-08-20)*

The persisted set gives up one good property: authority at the moment of serving. For most surfaces
that is fine — a stale coverage percentage is not a crisis. For one surface it deserves a second
thought: **private source code** (§1.1), which is both the sharpest disclosure in this document and the
place the app lends out its installation token.

Recommendation: on `GET .../file` **for a private repo only**, verify with a live
`GET /repos/{owner}/{repo}` as the user, cached 5 min per (user, repo). Viewing private source is
low-frequency, so this is on the order of one call per repo per 5 min per viewer — not bombardment —
and it means the one irreversible disclosure is never served on a snapshot that could be up to an hour
old. Public repos and every other endpoint stay fully local.

Flagged rather than assumed: it is a deliberate exception to D1 and the owner may prefer strict
consistency (no live calls anywhere). If declined, the TTL should be the lower end (30 min).

### D4 — Invert the source-content fetch order

`GitHubContentService` currently tries the installation token **first** and falls back to raw
(`GitHubContentService.cs:37-51`). Invert it: **raw first for public repos, installation token only for
a private repo whose viewer passed the per-repo check.** This narrows the confused-deputy surface to
exactly the case where we have verified the viewer, and it stops spending the installation's shared
GitHub quota on public files that `raw.githubusercontent.com` serves for free. Only one user-facing
call site exists (`BrowseController.cs:400`), plus `PublishFeedbackRecipient.cs:61` which is a
server-side config read and stays on the installation token. **[verified]**

### D5 — Anonymous access: keep public-repo read, tighten everything around it

This is a product decision, and the existing one stands: `PRD.md` §6.1 states *"public repo pages are
world-readable"*, and §6.4 builds the whole badge design on it. Codecov works the same way. So we do
**not** require sign-in for everything.

Worth recording how that switch would work if the owner ever wants it, because it is not obvious:
Spark has no `RequireAuthenticatedUser` knob (**[verified]** — it appears nowhere in the framework).
You achieve it by **granting no right to the `Everyone` group** in `security.json`; the default
`DefaultBehavior = DenyAll` (`Spark@Configuration/AuthorizationOptions.cs:17`) then denies anonymous
callers and every gated endpoint answers 401.

But keeping `Everyone` has a consequence Spark's own documentation spells out, **naming this repo**
(`Spark@docs/guide-row-security.md:98-117`):

> **When you grant `Everyone`, the row filter is the only thing between the public internet and the
> entire collection.** There is no second gate behind it. A bug that returns `null` instead of a
> restricting expression discloses **every row**. […] Row-level denial on this path is a filtered-out
> row, not an error — a mistake is silent. Test the anonymous case explicitly.

That is the risk we are accepting, so the mitigations in M2 (an Account row filter, redaction of
operational fields, and an explicit anonymous test matrix) are not optional polish — they are the
price of `Everyone`.

---

## 4. Findings inventory

Ranked. Everything **[verified]** unless marked. "New" = not previously recorded in `PLAN.md`,
`PRD.md` or `roadmap-2026-08.md`.

### Tier A — cross-tenant data disclosure

| # | Finding | Evidence | New |
|---|---|---|---|
| **A1** | **Private source-code disclosure across an org.** Owner-granular visibility + installation-token fetch ⇒ anyone reaching the `acme` installation reads every private `acme` repo's source. | `BrowseController.cs:439-452,400`; `GitHubContentService.cs:37-49` | ✅ (the *source* consequence; owner-granularity itself was known, `PRD.md:145`) |
| **A2** | **OIDC-provisioned repos are permanently marked public.** `IsPrivate = false` hardcoded, no installation ⇒ `OnRepository` never corrects it. Repo later made private ⇒ coverage, file tree and badge stay world-readable forever. | `UploadsController.cs:439-448`; `GitHubEventsRecipient.cs:109-158` | ✅ |
| **A3** | **Manage rights = read rights.** Any installation-reacher mints org-wide upload tokens and revokes others'. | `TokensController.cs:37,81,108`; `RepoSettingsController.cs:78` | ⬜ M9.29 / T2.3 |
| **A4** | **Every `Account` is anonymously enumerable**, including accounts that exist *only* because they own private repos. `AccountActions` declares no row filter (deliberately — "accounts are public data"), and `security.json` grants `QueryRead/Account` to `Everyone`. Second oracle: `GET /api/browse/accounts/{login}`. | `AccountActions.cs:8-19`; `security.json:6`; `BrowseController.cs:195-203` | ✅ |
| **A5** | **Home-card aggregates span repos the viewer may not see.** `RepoCount`/`AggregateCoverage` sum over *all* repos of an owner with no per-repo filter. Consistent under owner-granular rules; a leak the moment D2 lands. | `MeController.cs:49-63` | ✅ |

### Tier B — operational-data disclosure to anonymous callers

| # | Finding | Evidence | New |
|---|---|---|---|
| **B1** | **`Repository.Gate` is anonymously readable** via `GET /spark/po/Repository/{id}` — thresholds, targets, `Blocking` — while the `/api` route for the same data requires ownership. Only `BadgeToken` is redacted. | `RepositoryActions.cs:37-40`; `Model/Repository.json`; `RepoSettingsController.cs:38-45` | ✅ |
| **B2** | **CI internals on the anonymous Build grid.** `Build.Sessions` is a `Query` column and indexed, so `BuildSession.RootDir` (the runner's absolute `GITHUB_WORKSPACE`), `RawFileNames`, `JobName` and parse `Error` text reach anonymous grid rows. `GateSnapshot`, `Feedback` (check-run ids, error text), `DeclaredBaseSha` likewise on the detail PO. | `Model/Build.json`; `Actions/BuildActions.cs` (no redaction hook) | ✅ |
| **B3** | **`Account.InstallationId` is redacted but still an indexed, sortable attribute** — an anonymous caller can pass `?sortColumns=InstallationId:asc` and order by a field whose value is nulled. An ordering side channel. | `Model/Account.json`; `Spark@Endpoints/Queries/Execute.cs:30-77` | ✅ |
| **B4** | **`/health/ready` echoes exception text** including, on a misconfigured host, the **absolute path of the GitHub App private key**. Anonymous. | `Program.cs:278-285`; `GitHubAppReadinessService.cs:106-115` | ✅ |

### Tier C — surface and hardening

| # | Finding | Evidence | New |
|---|---|---|---|
| **C1** | **`POST /spark/auth/register` is mounted and anonymous.** `MapIdentityApi<SparkUser>` under `/spark/auth`, with `/login` and `/register` deliberately antiforgery-exempt; no email confirmation configured. A self-registered local account satisfies every `[Authorize]`. It stops short of impersonating an owner only because Identity forces `UserName == email` and a GitHub login cannot contain `@` — a string-format coincidence, not a control. | `Program.cs:54-81`; `Spark@SparkAuthenticationExtensions.cs:71,83,76-91` | ✅ |
| **C2** | **Degraded auth fails open to the claimed login.** A dead GitHub token or a GitHub outage ⇒ `Degraded(username)` ⇒ `owners = [ClaimTypes.Name]` with **zero** live verification. Bounded to one login, but it is an unverified grant that carries *write* rights (A3). Compounds with C3. | `GitHubAccessService.cs:44-48,90-91`; `GitHubUserTokenService.cs:46-48` | ✅ |
| **C3** | **`SparkUser.UserName` is captured once and never re-synced**, while `ClaimTypes.Name` is unconditionally trusted as an allowed owner. A GitHub rename that frees the login, taken over by another party, leaves the stale local account authorizing as that owner — including writes. | `Spark@SparkAuthenticationExtensions.cs:172-181`; `GitHubAccessService.cs:80-84` | ✅ |
| **C4** | **Five cookie-authenticated state-changing endpoints have no antiforgery.** `POST /api/tokens`, `DELETE /api/tokens/{hash}`, `POST .../settings/badge-token`, `PUT .../settings/gate`, `POST /api/me/accounts/resync`. `AddControllers()` adds no filter; Spark's CSRF gate fires only on endpoints carrying `IAntiforgeryMetadata`. Cookie `SameSite=Lax` blocks cross-site `fetch`, so this is defence-in-depth loss rather than live CSRF — but the highest-value action in the app is on the unprotected side. | `Program.cs:30-32`; `Spark@SparkMiddleware.cs:215-237` | ✅ |
| **C5** | **`X-Forwarded-For` is trusted from any peer** (`KnownNetworks`/`KnownProxies` cleared), so every IP-partitioned rate limit — `browse` 300/min, `badges` 600/min, the Spark bucket — is bypassable by rotating the header. Each uncached `/file` request spends the tenant's shared installation quota, which is the exact DoS the policy exists to prevent. | `Program.cs:23-28,255,186,198,232` | ✅ |
| **C6** | **`org rename` is unhandled and every join is on `Login`.** Identity is `GitHubId`, but `Repository.OwnerLogin`, `RepositoryVisibility.Filter` and `MeController` all join on the login string; `installation_target` is not handled. A rename silently detaches every repo from its viewers. | `GitHubEventsRecipient.cs:30-49`; `RepositoryVisibility.cs:36` | ✅ |
| **C7** | **`POST /api/github/webhooks` is not rate-limited** — Coverage narrowed `PathPrefixes` to `/spark`, `/connect`, `/api/browse`. Signature verification is sound and constant-time, but each request costs an HMAC over the full body first. | `Program.cs:101-102` | ✅ |
| **C8** | **`GetVisibleRepositoryIdsAsync` is unbounded** and feeds an `IN` clause covering *all* publicly-visible repos. Behaviour at Raven's clause limits **[unverified]** — see SP2. | `SparkVisibility.cs:31-35`; `CommitActions.cs:28` | ✅ |

### Verified clean — worth recording so it isn't re-audited

- **No IDOR in the `/api` surface.** Every `{owner}/{name}`, `{login}`, `{sha}`, `{path}`, `{flag}`,
  `{hash}` and `parentId` is either derived from an already-authorized document id or checked before
  use. Document ids are always composed from an authorized prefix, so no id injection escapes its
  repository scope.
- **Upload authorization is sound.** OIDC treats the *signed* `repository` claim as authorization and
  overrides body `run_id`/`run_attempt` from claims; `covt_` matches scope against `OwnerLogin`/`GitHubId`;
  unknown and unauthorized both return a bare 404 (`UploadsController.cs:372-403`).
- **Badge logic is sound.** Constant-time compare, never 404s, `Cache-Control` keyed on whether a token
  was presented rather than on the repo (no existence oracle) — `BadgeController.cs:51-77`. It leaks only
  via A2's stale flag.
- **Webhook signature verification is fail-closed** on both an empty secret and an empty header, and an
  unsigned request reaches zero recipients (`Spark@SparkWebhookEventProcessor.cs:44-52`).
- **Row security does not have a point-load bypass here.** Coverage's four Actions classes ignore the
  `action` argument, so the `"Read"` point-load gets the same predicate as `"Query"`. Spark compiles the
  filter expression and ANDs it with `IsAllowedAsync` on the detail path (`Spark@RowSecurity.cs:439-472`).
- **`ApiToken`, `FileCoverage` and `BuildTreeSummary` are outside the Spark model** (no model JSON), so
  they have no `/spark` surface at all.
- **The dev webhook WebSocket fails closed** in production — `AllowedDevUsers` is never set, so it is empty.

---

## 5. Spikes

Resolve these before or during the milestone that needs them. Each is cheap and each would otherwise
be resolved by guessing.

| # | Question | Why it matters | How to settle |
|---|---|---|---|
| **SP1** | ~~Does the bulk endpoint return per-repo `permissions` on an App user token, scopeless?~~ | — | **RESOLVED 2026-08-20 from the REST docs.** The endpoint is literally *"List repositories accessible to the **user access token**"*, and its description says verbatim: *"List repositories that the authenticated user has explicit permission (:read, :write, or :admin) to access for an installation. The authenticated user has explicit permission to access repositories they own, repositories where they are a collaborator, and repositories that they can access through an organization membership. **The access the user has to each repository is included in the hash under the permissions key.**"* That is exactly D1's requirement, stated by GitHub. **Scopes are a non-issue**: GitHub Apps do not use OAuth scopes at all — user-to-server tokens are governed by App *permissions* — which is why the sibling `GET /user/installations` already works today with no `Scope` configured anywhere in Coverage. ⚠️ **Doc trap avoided:** the *"This endpoint only works for PATs (classic) with the repo scope"* note on that page belongs to **"Add a repository to an app installation"**, a different endpoint — confirmed. Residual (self-answering on first run): whether a *selected-repositories* installation appears for a user with access to none of them. |
| **SP2** | What does RavenDB do with an unbounded `In()` over thousands of repo ids (C8)? Silent truncation, error, or fine? | `CommitActions`/`BuildActions` gate on it. Silent truncation would mean *missing* rows (fail-closed, so not a leak) but a broken product at scale. | Seed N repos in the embedded-Raven test host, binary-search N. |
| **SP3** | Does an org **owner** always get `admin: true` on every org repo in that response? | D2's Admin level, and SP5's `.All(...)` account-scope rule, both leaned on it. | **NOT settleable from docs, and the design no longer depends on it** (2026-08-20). GitHub documents owners as having *"complete administrative access to your organization"*, but does not state the repo-level `permissions.admin` value this response returns, so betting on the invariant would be betting on an inference. **Resolution: make the rule degrade instead of assuming.** Keep `.All(Admin)` for account-scoped minting, but (a) log the computed per-repo levels when it refuses, so a wrongly-refused owner is diagnosable in one log line rather than a support thread, and (b) if it proves too strict in practice, fall back to `GET /user/memberships/orgs/{org}` role=admin **for the account-scope case only** — a rare write path where one extra call is affordable. Verify empirically on first real use, not as a gate. |
| **SP4** | ~~`permissions.admin` or `permissions.push`?~~ | — | **RESOLVED 2026-08-20** (owner: no preference) → one `Admin` level = `permissions.admin`. See D2. |
| **SP5** | ~~What entitles minting an account-scoped token?~~ | — | **RESOLVED 2026-08-20** (owner: derive from GitHub but persist it) → `Admin` on **every** repo of that owner in the persisted set, computed locally, zero extra calls. See D1–D3. |
| **SP10** | ~~Which membership events can the App receive, under which permission?~~ | — | **RESOLVED 2026-08-20** by the owner inspecting and configuring the App: `Organization: Members: Read` + four subscriptions — **Member**, **Organization**, **Team**, **Team add**. Two invalidation shapes (targeted vs epoch) per D3. |
| **SP12** | ~~Is `Membership` subscribable?~~ | — | **RESOLVED 2026-08-20** — owner subscribed it. Handle as a **targeted** invalidation (payload names the user). Team-mediated access is now covered from both sides; TTL set to 60 min. |
| **SP13** | ~~Are `team` payloads resolvable to an `Account`?~~ | — | **RESOLVED 2026-08-20** by reflecting over `Octokit.Webhooks` 4.1.2 (the package already referenced) — see the payload table in D3. **Yes**: every relevant event carries `Organization` with an `Int64 Id`. Residual, cheap: confirm `Repository` is non-null on the `added_to_repository` / `removed_from_repository` actions specifically, which would allow a narrower invalidation than the account-wide epoch. |
| **SP14** | Can `My_Accounts` be made **synchronous**? It needs the viewer's id and their `UserAccess` snapshot; the id is available from `HttpContext.User` claims without awaiting, but the snapshot load is async. Determine whether pre-warming it makes a synchronous body possible, and whether `ISparkVisibility`'s task-memoized members are already complete by the time a custom query runs. | This is the entire user-visible win of M4 (finding D): `isAsync` gates declared sorting, row-filter pushdown, search pushdown, index projection and `.Include()`. If it cannot be made synchronous, header-click sorting stays inert and the PR should say so rather than let it look like an oversight. | Read what `QueryExecutor` awaits before invoking the method (`:235-330`), then try it. |
| **SP15** | Can a plain CLR row type (`MyAccountRow`) be exposed as an `IRavenQueryable<>` root on `CoverageSparkContext` and get **its own model file**, with no RavenDB collection behind it? Does `--spark-synchronize-model` generate one, does `modelHashes.json` accept it, and does the startup gate pass? | M4 step 3 — the only fix for the column leak (finding B) short of Spark#284. The pattern exists in the Vidyano reference apps (`Fleet/Service/FleetContext.cs:124-125` returns `null!` for a PO with no Raven backing), but Spark is unverified. | Add the root, synchronize, then boot in a **non-Development** environment — that is where the hash gate throws rather than warns. |
| **SP16** | Confirm the grid's aggregate semantics with the owner: is "repositories" **"those I am explicitly entitled to"** or **"those I can see"** (which includes public repositories of a reachable owner where the viewer holds no explicit permission)? | Finding C1. The two differ, today's code answers the second, and an index could only ever answer the first. It changes the number on screen, so it is a product decision — and it is *why* the index was rejected, not a detail of how to build one. | Owner's call. Recommendation: keep "those I can see", matching every other surface. |
| **SP11** | ~~Can per-installation granted permissions be read at runtime?~~ | — | **RESOLVED 2026-08-20 from the referenced assemblies.** `Octokit.Webhooks.Models.Installation.Permissions` is an `AppPermissions`, which exposes **`Members`** (alongside `Administration`, `Contents`, `Metadata`, …); `Octokit.InstallationPermissions` exposes `Members` too, for the REST path. So the flag is readable with no new dependency. ⚠️ **Implementation trap:** only the **`installation`** event carries the full `Installation` model. Every other event (`MemberEvent`, `TeamEvent`, `MembershipEvent`, …) carries **`InstallationLite`, which has just `Id` and `NodeId`** — no permissions. So read and persist `Members` on the `installation` / `new_permissions_accepted` events (already handled at `GitHubEventsRecipient.cs:66-72`) and store it on `Account`; do not expect to read it off the event you happen to be handling. |
| **SP6** | Does a Spark **custom query** return rows when `parentId`/`parentType` are bound to empty strings from a page with no parent PO? | The accounts-grid design depends on a parentless `spark-sub-query`. Read as correct on both halves of the wire (`SparkService.executeQuery` only appends truthy params; `Spark@Endpoints/Queries/Execute.cs:96-108` skips parent resolution when either is empty) — but it has never been *run*. | Declare the query, bind `parentId=""`, load the page. |
| **SP7** | Is `POST /spark/auth/register` actually reachable on the deployed instance (C1)? | Determines whether C1 is a live surface or only a mapped one. **[unverified]** — the code maps it; production behaviour untested. | `curl -X POST https://coverage.mintplayer.com/spark/auth/register …` against production. |
| **SP8** | Can `spark-sub-query`'s column set be curated **per query** rather than per entity, or does adding `RepoCount`/`AggregateCoverage` to `Account` also add them to the global accounts grid? | Already filed upstream as [Spark#284](https://github.com/MintPlayer/MintPlayer.Spark/issues/284). If unresolved, the global grid gains two columns — acceptable, but it should be a decision not a surprise. | Check #284's status; otherwise accept and note it. |
| **SP9** | Declared `sortColumns` are **silently ignored** for custom queries returning `Task<...>` (`Spark@QueryExecutor.cs:428,322-326` — `IsQueryable = !isAsync && …`). Is this a Spark bug worth filing? | It is a **live latent bug in this repo already**: `Model/Commit.json:229-234` declares `sortColumns: Date desc` on `Repository_Commits`, which returns `Task<IQueryable<Commit>>`, so the declared sort never runs and header-click sorting is a no-op — it only looks right because the method itself orders. | Confirm, then file upstream. Meanwhile return a **synchronous** `IQueryable` from the accounts query. |

---

## 6. Milestones

Sequenced so each stage is independently shippable and the disclosure paths close first. Legend:
🟦 Coverage · 🟩 Spark PR.

### M0 — Re-key identity on `GitHubId` 🟦 · cost S–M · **do this first**

*Added 2026-08-20 once breaking changes were sanctioned.* This was originally M6, sequenced last
because re-keying is risky when you must preserve existing rows. With no users to preserve, it becomes
cheap — and doing it **before** M1 means the new entitlement store is keyed correctly from birth
instead of being built on logins and migrated later.

The defect: identity is `GitHubId` (`Account.DocumentId`), but every *join* is on the mutable `Login`
string — `Repository.OwnerLogin`, `RepositoryVisibility.Filter:36`, `MeController.cs:45-52`,
`IsOwnerAllowedAsync` everywhere. That single mismatch causes three findings: **C6** (an org rename
silently detaches every repo from its viewers), **C3** (a freed login taken over by another party
authorizes as that owner, including writes), and it would put a mutable string in the middle of
`UserAccess.Owners`.

1. Joins move from `OwnerLogin` to the account document id / `GitHubId`. `RepositoryVisibility` is the
   single definition point (its own doc-comment says so), so the surface is smaller than it looks.
2. Stop trusting `ClaimTypes.Name` as an allowed owner (C3); derive the caller's own account from the
   stored GitHub **numeric id** instead. This is the fix I hedged on in the first draft — with no users
   to re-key it is simply the right one.
3. Handle `installation_target` for org renames (C6). `Login` stays as a display field, refreshed on
   rename, and is never load-bearing again.
4. Rebuild rather than migrate where that is simpler — sanctioned.

**Exit criteria**: renaming an org on GitHub changes only display text; no gate anywhere consults a
login string.

### M1 — Close the source-code disclosure 🟦 · cost M

The narrowest change that stops A1 and A2. Deliberately does not refactor the whole entitlement model.

1. **The `UserAccess` document + its builder** (D1, D3): `GET /user/installations` →
   `GET /user/installations/{id}/repositories` per installation → persist `Owners`, per-repo `Level`,
   `BuiltAtUtc`, `BuiltAgainstEpochs`. Depends on **SP1**, **SP3**. Refresh on sign-in, on resync, and
   lazily on TTL expiry.
2. **`IRepositoryAccessService`** — one method, `Task<RepositoryAccess> GetAsync(Repository repo, CancellationToken ct)`
   returning `None | Read | Admin`, reading the persisted document (one point-load) and the repo-level
   public flag. This is the single seam every gate moves onto, so it is worth getting the interface
   right before the call sites move.
3. `Repository.VisibilityCheckedAtUtc` + re-verify a believed-public repo whose stamp is stale, before
   serving it. Closes A2 for every reader at once, since all seven `IsPrivate` gates route through it.
4. `ResolveVisibleRepository` (`BrowseController.cs:439-452`) calls the new service instead of
   `RepositoryVisibility.IsVisible`. This one edit covers all nine `/api/browse` endpoints, since every
   one of them already funnels through it.
5. Invert the content fetch per **D4**; add the private-source live check per **D3a** if accepted.
6. `Account.AccessEpoch`, bumped by the `installation` / `installation_repositories` recipients
   (`GitHubEventsRecipient.cs:57-107`), and the epoch-mismatch staleness check.
7. Backfill: stamp `VisibilityCheckedAtUtc = null` on every existing `Repository` so all are re-verified
   once on first access. A migration in the style of `Migrations/M_*.cs`. `UserAccess` needs no backfill
   — a missing document simply means "build on next use".
8. **Badge on a private flip** (owner's decision): when re-verification finds a repo has become private,
   the badge renders `unknown` — no distinct "private" label, since PRD §6.4 rules that out as an
   existence oracle — and the repo page surfaces *why*, with a pointer to minting a badge token. Put the
   warning next to the last-sync/degraded indicator roadmap T0.2 is adding rather than inventing a
   second place for it. **Non-blocking**: with no real users, the `unknown` behaviour can ship in M1 and
   the warning surface can follow, if splitting them is convenient.

**Note on the `IMemoryCache` this replaces.** `GitHubAccessService`'s 5-min per-process cache
(`:25,86`) becomes redundant for entitlement. Retire it rather than layering the new store on top —
two caches with different lifetimes over the same question is how the 5-min window and the 30–60-min
window would silently disagree.

**Exit criteria**: an outside collaborator on one public repo of an org gets 404 on a sibling private
repo's `/file`, `/tree`, `/hierarchy` and `/commits/{sha}`; a repo flipped to private on GitHub stops
serving within the TTL even with no installation and no webhook.

### M2 — Close the anonymous surface 🟦 · cost M

Everything anonymous callers should not see. Independent of M1; can run in parallel.

1. **Account row filter** (A4). Requires a denormalized `Account.PublicRepoCount` maintained on repo
   upsert — the filter must be RavenDB-translatable, so it cannot join. Filter shape:
   `a => a.PublicRepoCount > 0 || a.Login.In(owners)`. Apply the same rule to
   `GET /api/browse/accounts/{login}`.
2. **Redact operational fields** (B1, B2) via `GetProtectedAttributesAsync` on `RepositoryActions`
   (add `Gate`) and a new hook on `BuildActions` (`Sessions`, `GateSnapshot`, `Feedback`,
   `DeclaredBaseSha`). Note Spark's redaction **nulls, it does not omit**, and dotted names
   (`"Sessions.RootDir"`) recurse into AsDetail rows — that is the mechanism for B2's nested fields
   (`Spark@RowSecurity.cs:401-431`).
3. **`InstallationId` out of the index** (B3) — `[IgnoreForIndex]`, the same treatment `BadgeToken` and
   `Gate` already have (`Repository.cs:38-49`). Removes the sort channel rather than papering over it.
4. **`/health/ready` stops echoing exception text** (B4) — log the detail, return a classification.
5. **An explicit anonymous test matrix.** Non-negotiable per D5: for every `/spark` query and every
   `/api/browse` route, assert what an anonymous caller and a non-member authenticated caller get.
   This is the test suite that would have caught A4 and B1.

**Exit criteria**: an anonymous caller can enumerate only accounts with ≥1 public repo, sees no gate
policy, no runner paths, no check-run ids; the matrix is green and runs in CI.

### M3 — Split read from manage 🟦 · cost M

1. `TokensController` (`:37,81,108`) and `RepoSettingsController` (`:78`) move from `IsOwnerAllowedAsync`
   to the `Admin` level. Depends on **SP3**, **SP4**.
2. Account-scoped token minting per **SP5**.
3. `AccountActions.GetProtectedAttributesAsync` and `RepositoryActions`' `canManage` move to `Admin`,
   so the UI stops offering management affordances it will then refuse.
4. **Retire M9.29 and roadmap T2.3's `read:org` approach** in those documents, pointing here.

**Exit criteria**: a read-only org member gets 403 on token minting and badge rotation, and the UI does
not show the controls.

### M4 — The accounts grid 🟦 · cost M

Item (2) of the original ask. **Revised 2026-08-21 after a three-agent investigation** of Spark's
index/query pipeline, the two Vidyano+CronosCore reference apps (`C:\Repos\Fleet`,
`C:\Repos\Insurance`), and a design review of the owner's proposed multi-map index. The revision
matters: the first version of this milestone was building on two false premises.

#### What the investigation overturned

**A. An index buys sorting, not paging.** Spark pages **in memory on every path** — `Database.*` and
`Custom.*` alike: `QueryExecutor.cs:69-73` materializes the whole result, counts it, then
`Skip().Take()`. `CustomQueryArgs` (25 lines) exposes only `Parent`/`ParentType`/`Query`, so a custom
query cannot page even voluntarily. No index design changes this. State the goal as *sorting*.

**B. `[IgnoreForIndex]` is not the column-leak fix, and the leak is live.** Spark never reads
Newtonsoft's `[JsonIgnore]` — model membership is `IsSparkModelProperty`
(`Abstractions/Reflection/ReflectedTypeExtensions.cs:114-124`) — so `RepoCount`,
`AggregateCoverage` and `IsAppInstalled` really are mapped into `VAccount`, and curating them to
`showedOn: "Query"` puts three meaningless columns on the **anonymous** `GetAccounts` grid. Marking
them `[IgnoreForIndex]` would narrow `showedOn` to `PersistentObject`, removing them from *every*
grid including this one. With today's Spark you cannot have a column on one Account grid and not the
other — that is SP8 / [Spark#284](https://github.com/MintPlayer/MintPlayer.Spark/issues/284).

**C. The proposed multi-map/reduce index does not work.** Three independent blockers:

1. **It cannot express the visibility rule.** `RepositoryVisibility.Filter` is
   `!IsPrivate || GitHubId.In(entitled)`. `UserAccess.Repositories` carries only the second
   disjunct — `GitHubEntitlementSource` fills it from `/user/installations/{id}/repositories`, i.e.
   repositories with *explicit* permission. A **public** repository of a reachable owner on which the
   viewer has no explicit permission is counted today and would be **silently dropped** by an index
   fanning out that list. Closing the gap needs "every repository where `OwnerGitHubId == X &&
   !IsPrivate`", which is a *query*: map-reduce is a `GROUP BY`, not a `JOIN`, and `LoadDocument`
   takes an id, so there is nothing to join from. Failure mode: a wrong number on screen, no error.
2. **Spark drops the rows.** `AccountActions` overrides **both** row hooks, so a `[FromIndex]`
   projection on `Account` takes the projecting branch of `RowSecurity.FilterAsync:196-206`, where a
   row whose `Id` names no document is `continue`d — and `RedactAsync:318-326` blanks every attribute
   of such a row instead. That is exactly the synthetic rows for owners with no `Account` document
   that `AccountActions.Row` exists to produce.
3. **`LoadDocument<Account>` would be a re-index storm of our own making.** RavenDB re-indexes every
   source document that referenced a changed document, re-running its whole map. `Account` is among
   the most-written documents here: `AccessEpoch` bumps on installation/team webhooks (D3) and
   `AccountPublicityService.ReconcileAllAsync` rewrites `PublicRepoCount` **every five minutes** from
   `RefreshVisibilityLeasesCronJob`. Never put `Account` in this index.

**D. The user-visible win is one keyword.** `QueryExecutor.cs:425-426` computes
`isRavenQueryable`/`isQueryable` as `!isAsync && …`, and `isAsync` gates **more than SP9 recorded**:
declared `sortColumns` (`:322-326`), row-filter pushdown (`:309-312`), search pushdown (`:317-320`),
index projection (`:291-294`) *and* `.Include()` (`:297-305`) are all skipped for a
`Task<>`-returning custom query. This milestone's own step 1 already said "return a **synchronous**
`IQueryable<Account>`"; the code shipped `async Task<IQueryable<Account>>` and therefore has no
sorting. Making it synchronous is the whole win.

#### Decision

**Compute the rows in memory; do not build the index.** It is the only option that preserves A5's
per-viewer correctness with **one** definition of visibility (`RepositoryVisibility.IsVisible`, shared
with the row filter, so drift is impossible) and zero staleness, and the sorting complaint is
addressed by D rather than by an index. The escalation path is recorded below for when — if ever —
the grid outgrows it.

Rejected alternatives, with the reason each fails, so this is not relitigated: the proposed
multi-map/reduce (C1–C3); a single-map + `LoadDocument` variant (same C1/C3); and denormalizing the
aggregates onto `UserAccess` at rebuild time — which looks attractive until you notice a coverage
upload bumps neither the TTL nor `AccessEpoch`, so the app's headline number would be **up to 60
minutes stale with no signal**. That last one is the most tempting and the most dangerous.

#### Steps

1. **Make `My_Accounts` synchronous** (`AccountActions.cs`). Resolve the viewer from
   `HttpContext.User` claims (no await) and pre-warm the `UserAccess`/visibility snapshot so the
   method body needs no `await`; return `IQueryable<Account>`. Then declare `sortColumns` in the query
   and drop the in-method `OrderBy` once header-click sorting is verified. Depends on **SP14**.
2. **Retro-apply the same fix to `CommitActions.Repository_Commits`**, which declares
   `sortColumns: Date desc` that has never run (SP9) — it only looks correct because the method
   orders internally.
3. **Give the row its own model entity type** (`MyAccountRow`), exposed as an `IRavenQueryable<>`
   root on `CoverageSparkContext` so `ModelShapeDiscovery.QueryableRoots` gives it its own model file
   and its own three columns — then **remove `RepoCount`/`AggregateCoverage`/`IsAppInstalled` from
   `Account`**, which also removes three meaningless columns from the anonymous `GetAccounts` grid
   (finding B). Depends on **SP15**. If SP15 says no, accept the leak and track Spark#284 instead;
   do not reach for `[IgnoreForIndex]`.
4. **Aggregate over the *visible* repository set** — `RepositoryVisibility.IsVisible`, not the
   owner's whole set. This is finding A5 and it is the reason this milestone is a security item and
   not a cosmetic one.
5. Replace the card body with `<spark-sub-query queryId="my-accounts" parentId="" parentType="" />`
   (SP6). The component renders its own `bs-card` + header, so the existing card wrapper comes out.
6. Keep the grid to **three or four columns**. Column count, not CSS, is what makes this usable on a
   phone — see the honest note below.

**Honest note on mobile, unchanged from the first draft.** The Spark datatable is *not* responsive:
its entire shipped stylesheet contains one at-rule (`prefers-reduced-motion`), and `[isResponsive]` is
a dead input whose own JSDoc calls it "legacy […] currently a CSS hook". What it *does* fix is exactly
the reported complaint — `white-space: nowrap` + `text-overflow: ellipsis` make the mid-word fracture
impossible and rows uniform height, with overflow confined to the grid's own `overflow: auto` box.
What you get instead is a shrunken desktop table behind a horizontal swipe, with sub-44px touch
targets. The win is real but it is a *change* of failure mode; step 6 is what buys mobile quality.

**Cheap interim, if M4 slips.** `home.component.html:53` sets `d-flex` with no `flex-wrap` (the
sibling reauth alert 36 lines above *does* have it), while `.card { word-wrap: break-word }` lets the
over-constrained line fracture mid-word and the badge's `white-space: nowrap` refuses to shrink.
Adding `flex-wrap` there plus `text-nowrap` on the badge stops the fracture in two classes.

#### Escalation path, if the grid ever outgrows in-memory computation

Only for a viewer with thousands of owners, which is not a real shape today. Recorded so the design
is not rediscovered from scratch, and shaped to avoid all three blockers:

- Store the per-owner **visible repository id list** on `UserAccess` at rebuild time, computed in C#
  with `RepositoryVisibility.IsVisible` — so visibility still has exactly one definition, and the
  index only aggregates.
- Aggregate with `AbstractMultiMapIndexCreationTask<MyAccountRow>` + `Reduce`. **Never** the two-arg
  `AbstractIndexCreationTask<TDoc,TReduce>`: Spark's catalog matches only the one-generic-argument
  forms (`SparkMiddleware.cs:532-548`, `IndexCatalog.cs:214-236`), so the two-arg form is *deployed
  by `IndexCreation.CreateIndexes` but invisible to the catalog*, and a query naming it throws.
  Measured against Raven 7.2.5 — and note Spark's own `DefaultIndexAnalyzer.cs:136-138` comment
  asserts the opposite, which is 🟩 upstream bug report **U3**.
- `LoadDocument` **only `Repository`**, never `Account` (blocker C3). Freshness then rides on
  reference re-indexing: seconds, not the 60 minutes a denormalized field would cost.
- Materialize with `OutputReduceToCollection` so rows are real documents. That restores everything
  blocker C2 takes away: a `UserId` row filter that composes server-side, working redaction, a
  clickable detail page, and its own model file (so no column leak). Caveats to verify first:
  artificial documents are per-node and not replicated, and the row type needs its own `security.json`
  grant — where `Everyone` is the only group today, making the `UserId` filter the sole gate.
- Follow the reference apps' **zero-seed** pattern
  (`Insurance.Library/Indexes/InsurancePolicyDocumentTypes_InsurancePolicyDocumentCount.cs`): map 1
  seeds `count = 0` from the parent collection, map 2 emits `1` per child, reduce sums. Without the
  seed, an account with no visible repositories produces no reduce group and **vanishes from the
  grid**. `Insurance.Library/Indexes/People_DKVErrors.cs:705-716` documents the same trap from the
  other side.
- `StoreAllFields(FieldStorage.Yes)` is mandatory and is unconditional house style in both reference
  apps. Spark calls `ProjectInto<T>()` for you (`QueryExecutor.ApplyProjection`), so never write it
  in app code on these paths.
- Reduce-computed columns must be **hand-added** attributes in the model JSON with
  `isRequired: false` — the synchronizer preserves them (`ModelSynchronizer.cs:708-750`) but never
  generates them, and `isRequired` would make `ValidationService` block every save of the type.
- The index must be registered when `--spark-synchronize-model` runs, or the synchronizer **silently
  retargets the query's `indexName` to the entity default** (`ModelSynchronizer.cs:148-158`).
- Never make the reduce index the entity's `[DefaultIndex]`: the PO-list path hardcodes
  `isMapReduce: false` (`DatabaseAccess.cs:419`).
- **We would be the first.** `grep -rn "Reduce\s*=|OutputReduceToCollection"` across all of Spark —
  libs, tests, and every demo app — returns **zero hits**. Multi-map *registration* is tested;
  multi-map *querying* is not tested at all. A verification spike is mandatory, not optional.

### M5 — Surface hardening 🟦 · cost S–M

1. **Disable the local-password surface** (C1) — 🟩 **handed off upstream 2026-08-20**. Spark will
   introduce a feature to disable these endpoints **and the client-side pages**; when it ships, adopt it
   and delete any stopgap. This is the layering rule working correctly: "no local logins, GitHub only"
   is a generic Spark concern, not a coverage-domain one.

   **The client half needs an *omit* capability, not a config knob — worth passing to the Spark work.**
   `app.routes.ts:12` mounts `...sparkAuthRoutes()`, and reading
   `Spark@libs/node_packages/ng-spark-auth/routes/src/spark-auth-routes.ts:47-61` shows it returns **all
   five routes unconditionally**: `login`, `login/two-factor`, `register`, `forgot-password`,
   `reset-password`. The `SparkAuthRouteConfig` parameter only lets a consumer **rename the path** or
   **swap the component** (`resolveEntry:8-25`) — there is no way to *not mount* a route. So a consumer
   that wants GitHub-only login cannot express it today at any price, and the upstream feature has to add
   omission (e.g. `false` per entry, or an allow-list), not just more path configuration.

   Note also `SPARK_AUTH_ROUTE_PATHS` (`:39-45,51`) is injected from those same paths, so whatever
   omission mechanism lands must keep that token coherent — components that link to a now-omitted route
   need to not render the link rather than link into a 404.

   **Stopgap, only if this is needed before the upstream feature lands:** short-circuit the
   `/spark/auth/*` paths in a small terminal middleware *before* the endpoint stage. Paths:
   `register`, `login`, and the password-recovery set (`forgotPassword`, `resetPassword`, `confirmEmail`,
   `resendConfirmationEmail`) — leaving those live keeps an account-enumeration oracle open even with
   register closed. ⚠️ Do **not** shadow by re-registering the same route templates ahead of
   `MapSpark()`: two endpoints with the same template and method raise an `AmbiguousMatchException` at
   request time, so the "404" becomes a 500 that only shows up when someone hits the path. The
   middleware cannot ambiguously match and keeps working if Spark adds a route under `/spark/auth`.
2. **Antiforgery on the five MVC write endpoints** (C4) — `[ValidateAntiForgeryToken]` or an
   `AutoValidateAntiforgeryTokenAttribute` convention, plus confirming the SPA sends the header.
3. **Trust `X-Forwarded-For` only from the real proxy** (C5) — set `KnownProxies` to the deployment's
   ingress instead of clearing it. Restores every IP-partitioned limit.
4. **Rate-limit the webhook path** (C7).
5. **Fail closed on degraded auth** (C2) — a dead GitHub token should not yield an *unverified* grant to
   the claimed login. With M1's per-repo check this shrinks naturally (private access requires a live
   call that will fail), but the own-login grant should become explicit and read-only.
6. *(C3 / C6 — moved to **M0**, which fixes them at the root by re-keying on `GitHubId` rather than
   patching the login-resync path.)*

### M6 — Membership webhooks 🟦 · cost M

D3 mechanism 3 — the owner's suggestion. Turns revocation from TTL-bounded into near-immediate.
Sequenced after M1 because it *invalidates* the `UserAccess` document M1 introduces; without M1 there
is nothing for it to invalidate.

1. **Grant `Organization: Members: Read`** on both GitHub Apps (`coveragedevelopment`,
   `coverageproduction`) and subscribe **Member**, **Organization**, **Team**, **Team add** — already
   done by the owner (SP10, SP12). Confirm deliveries arrive on dev before touching production.
2. Recipients in `GitHubEventsRecipient` (the `switch` at `:30-49`), in **two shapes** — invalidate,
   never patch (D3):
   - `member`, `organization`, `membership` → **delete that user's `UserAccess`** (payload names them).
   - `team`, `team_add` → **bump `Account.AccessEpoch`** (payload does not). Depends on **SP13** for
     resolving the account, and must log loudly rather than `return` if it cannot.

   Note the existing `default: return` drops unhandled events **silently**, so a missing `case` looks
   exactly like a working system. Add all five cases together, each with a test, and assert the
   *shape* — a `team` event that took the targeted path would compile and do nothing useful.
3. **Surface the migration state** per **SP11**: record whether each installation has accepted the new
   permission, shorten the TTL for those that haven't, and show it wherever the last-sync timestamp
   ends up living (roadmap T0.2 is adding one — the two belong side by side).
4. *(`installation_target` / org renames — moved to **M0**, where the identity re-keying lives.)*

**Exit criteria**: removing a user from an org, or removing their collaborator access to a repo, revokes
access within seconds; removing a *team's* access to a repo revokes it for every member of that team
within seconds via the epoch, with no member lookup; a non-migrated installation degrades to the TTL
with the difference **visible in the UI** rather than silent.

---

### M7 — Bound OIDC auto-provisioning 🟦 · cost M

Owner's decision: keep auto-provisioning, add a quota. This is roadmap **T0.3**'s first bullet, now
decided — that item already scoped "a per-account storage quota at upload, a raw-attachment TTL, and a
stricter tier for auto-provisioned repos", so implement it there rather than duplicating it here.

1. A per-account cap on repos and stored bytes for **provisioned-but-uninstalled** repos, stricter than
   for installed ones. `ResolveOidcRepository` (`UploadsController.cs:429-448`) is the enforcement point.
2. A raw-attachment TTL, per T0.3.

Reconsider-if: the argument for keeping auto-provisioning was onboarding friction, which is weak with no
users yet. If the quota turns out to be more than an M, **requiring the App to be installed** is now a
cheap alternative that deletes A2's root cause instead of bounding it — revisit rather than grind.

---

## 6a. Upstream Spark asks

Generic concerns, so they belong upstream per the layering rule — one PR per repo. None blocks the
revised M4; U1 unblocks a design option, U2/U3 are correctness reports.

| # | Ask | Why it is Spark's problem, not Coverage's |
|---|---|---|
| **U1** | **Drop the parameterless-constructor requirement on `SparkContext`.** `SparkMiddleware.cs:154` registers the context via DI (so constructor injection works at runtime), but the offline model commands build it with `Activator.CreateInstance` and *check* for a public parameterless ctor, exiting `ExitMisconfigured` (`SparkDevelopmentExtensions.cs:312-321`); the generic overload additionally constrains `where TContext : SparkContext, new()` (`:82`). The code's own comment says the synchronizer "reflects over the context's property **TYPES** and never invokes a getter", so the instance is only a carrier for its `Type`. Fix: take a `Type`, or use `RuntimeHelpers.GetUninitializedObject`. | Until this lands, no consuming app can put a request-scoped dependency (a current user, a tenant) on its context — the natural home for a "my …" query. |
| **U2** | **`AbstractIndexCreationTask<TDoc,TReduce>` is deployed but invisible to the index catalog.** `IsAbstractIndexCreationTask` (`SparkMiddleware.cs:532-548`) and `GetCollectionTypeFromIndex` (`IndexCatalog.cs:214-236`) match only the one-generic-argument forms, while `IndexCreation.CreateIndexes` (`:522`) deploys everything. So the *standard* map-reduce base class yields a live index no query can bind to, and the failure is a throw at query time rather than at startup. | It is the most natural way to write a map-reduce index, and the trap is silent until a query names it. |
| **U3** | **`DefaultIndexAnalyzer.cs:136-138` states the base-type relationship backwards.** Its comment claims the two-argument form derives from the one-argument form "so the walk covers it". Measured against Raven 7.2.5 the reverse is true: `AbstractIndexCreationTask<T>` derives from `AbstractIndexCreationTask<T,T>`. That comment is what would lead a maintainer to believe U2 is already handled. | A wrong comment in the analyzer guarding this exact area. |
| **U4** | *(already filed)* [Spark#284](https://github.com/MintPlayer/MintPlayer.Spark/issues/284) — per-query grid columns. Spark derives `showedOn` from projection-vs-entity membership (`ModelSynchronizer.cs:585-601`), so a column cannot appear on one query of an entity and not another. This is what forces M4 step 3's separate row type. | Without it, every computed column added for one grid leaks onto every other grid of the same entity. |

---

## 7. Test plan

The anonymous matrix in M2.5 is the centrepiece — it is the test that fails on the whole class of bug
this document is about, and its absence is why A4/B1/B2 went unnoticed. Concretely, per surface
(`/spark` queries × `/api/browse` routes × the badge) assert the result for five callers:

| Caller | Expectation |
|---|---|
| anonymous | public repos only; no gate, no runner paths, no accounts without public repos |
| authenticated non-member | identical to anonymous |
| outside collaborator on one public repo of the org | that repo only — **not** its private siblings (the A1 regression pin) |
| org member with read access | their repos; no management affordances (A3 pin) |
| org admin | full, including management |

Integration tests need `MintPlayer.Spark.Testing` with embedded RavenDB. `PLAN.md` M9.26 leaves this ⏳
"pending `RAVENDB_LICENSE` provisioning in CI" — **that blocker is gone**: the organization has a
**org-wide `RAVENDB_LICENSE` secret** (owner, 2026-08-20), so it only needs referencing as
`secrets.RAVENDB_LICENSE` in the workflow. This plan therefore has **no external prerequisite**, and
M9.26 should be re-marked in `PLAN.md` when the first suite lands.

Two things to get right while wiring it:

- **Fork PRs do not receive secrets.** The integration suite will skip (not fail) on fork PRs, so it
  cannot be the only gate on a protected branch — keep the unit suite as the universal gate and let the
  integration suite run on branch pushes and same-repo PRs.
- **The GitHub calls in D1/D3a must sit behind an interface with a fake**, or the anonymous matrix is
  untestable and the tests end up depending on live GitHub state.

Per the global test policy: verify intermediate milestones by build + type-check, and batch the full
suite into one sweep per milestone before its PR.

---

## 8. Explicitly out of scope

- **An internal ACL.** Still rejected, and the distinction from D3 matters. What roadmap §6 rejected was
  an ACL as an *independent source of truth* — one that can be edited in-app, drifts from GitHub, and
  needs its own administration UI. `UserAccess` is a **derived cache with a TTL and an invalidation
  path**: nothing writes to it but the GitHub sync, nothing reads it as authority, and deleting it costs
  one refresh. GitHub remains the authority. If a future change ever lets a human edit it directly,
  that is the moment this rejection has been violated.
- **A join-request / approval workflow.** `PRD.md` §6.3 rejected it; §2 confirms the code already
  behaves correctly without one.
- **Per-repo entitlement for *list* queries.** D3 keeps owner-granularity for listing deliberately — a
  list cannot afford a per-row decision. Lists may over-include; serving may not.
- **A GDPR/offboarding path for `UserAccess`.** Out of scope here, but note it: `UserAccess` records
  which repositories a person can reach, so it joins the set of documents roadmap T2.2's deletion/export
  work must cover. One line in that plan, not this one.
- **Fixing Spark's `Task<>` sort bug and `SelectionRule` non-enforcement.** File upstream (SP9); don't
  block on it.
- **`GetVisibleRepositoryIdsAsync` unbounded `IN`** (C8) — measure via SP2, fix only if SP2 says it bites.

---

## 9. Backlog items this supersedes

| Existing item | Disposition |
|---|---|
| `PLAN.md` M9.29 — admin gating via `GET /user/memberships/orgs/{org}` role=admin | **Superseded by D1/D2** — the persisted `permissions` hash answers it per-repo, needs no `read:org`, and costs nothing at request time. The `memberships/orgs` call is dropped entirely. |
| `PLAN.md` M9.26 — "⏳ integration tests pending `RAVENDB_LICENSE` provisioning in CI" | **Stale** — the org-wide secret exists (owner, 2026-08-20). Re-mark when the first suite lands. |
| roadmap T2.3 second paragraph — same | Same. |
| `PRD.md` §6.1 "Visibility is owner-granular […] there is no per-repo check" | **Rewrite** when M1 lands — it documents the defect as the design. |
| `PRD.md` §6.3 | **Unchanged and confirmed** — §2 above is its verification, not its replacement. |
| roadmap T0.3 third bullet — `BrowseController` has no `[Authorize]`/rate limiting | Partly here (C5 restores the limiter's integrity); the bound itself stays in `upload-result-contract.md` N5 as sequenced. |
