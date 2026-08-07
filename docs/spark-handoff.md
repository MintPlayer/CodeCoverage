# Handoff: MintPlayer.Spark work for the Coverage project (M0)

Work items for a Claude session running in `C:\Repos\MintPlayer.Spark`. Discovered
during the Coverage-analyzer investigation (see `docs/PRD.md` in MintPlayer/CodeCoverage,
§10 and PLAN.md M0). Branch from `master` (note: local checkout sits on `security-audit`,
one docs-only commit ahead — confirm intended base). One PR for the lot.

## 1. Bug: typed webhook messages produce invalid queue names (likely High)

**Symptom**: any app with a typed `IRecipient<GitHubWebhookMessage<TEvent>>` faults at
startup: the worker's queue-name allowlist rejects the name.

**Cause** (verified in source):
- `MessageBus.StoreMessageAsync` (libs/messaging/MintPlayer.Spark.Messaging/Services/MessageBus.cs:34-36)
  and `MessageSubscriptionManager.DiscoverQueueNames` (Services/MessageSubscriptionManager.cs:107-108)
  both fall back to `messageType.FullName` when there's no `[MessageQueue]`.
- For a closed generic, `FullName` embeds assembly-qualified args:
  `Ns.GitHubWebhookMessage`1[[Octokit.Webhooks.Events.PullRequestEvent, Octokit.Webhooks, Version=…]]`
  — contains `[ ] , =` and spaces.
- `MessageSubscriptionWorker.IsValidQueueName` (Services/MessageSubscriptionWorker.cs:60-73,
  added by R2-H14) rejects those chars; `ConfigureSubscription` throws at startup, faulting
  the manager via `Task.WhenAll`.
- `GitHubWebhookMessage<TEvent>` (libs/webhooks/.../Messages/GitHubWebhookMessage.cs:28) has
  no `[MessageQueue]`; only the non-generic catch-all does (`spark-github-all`).

**Proposed fix** (drafted, reviewed design): a single internal `QueueNames` class in
`MintPlayer.Spark.Messaging/Services` owning both derivation and validation:

```csharp
internal static class QueueNames
{
    public static string ForMessageType(Type messageType)
    {
        var attr = messageType.GetCachedCustomAttribute<MessageQueueAttribute>();
        return attr?.QueueName ?? Derive(messageType);
    }

    // FullName of a closed generic embeds assembly-qualified args whose
    // '[', ']', ',', '=' and spaces IsValid rejects. Derive from the definition's
    // FullName + recursively-derived arg names so every CLR type yields a valid,
    // deterministic name (both bus and manager derive identically).
    private static string Derive(Type type)
    {
        if (!type.IsGenericType) return type.FullName!;
        var definitionName = type.GetGenericTypeDefinition().FullName!;   // "Ns.Message`1"
        var argumentNames = string.Join("-", type.GetGenericArguments().Select(Derive));
        return $"{definitionName}-{argumentNames}";
    }

    public static bool IsValid(string value) { /* move IsValidQueueName here verbatim */ }
}
```

- Use in `MessageBus.StoreMessageAsync` and `MessageSubscriptionManager.DiscoverQueueNames`
  (replacing both `FullName` fallbacks) and `MessageSubscriptionWorker.ConfigureSubscription`
  (calls `QueueNames.IsValid`).
- Non-generic names are unchanged (`FullName`, incl. nested `Outer+Inner`) — the existing
  `MessageBusTests.BroadcastAsync_persists_a_SparkMessage_with_inferred_queue_name_and_payload`
  stays green.
- Tests to add (xunit + FluentAssertions, `tests/MintPlayer.Spark.Tests/Messaging/`,
  InternalsVisibleTo already present): closed-generic type → name passes `IsValid`;
  bus + manager agree for the same closed generic; `[MessageQueue]` still wins; nested
  generic args. Repro/regression: boot check or E2E asserting a typed
  `IRecipient<GitHubWebhookMessage<PullRequestEvent>>` app starts and receives events.
- Cleanup while there: `Messages/GitHubQueueNames.cs` is dead code (zero call sites) and
  the README queue tables (libs/webhooks/.../README.md:258-262, 481-493) describe that
  abandoned scheme — delete/fix.

## 2. New library: API tokens (PAT) for CI upload authentication

Spark has zero API-token infrastructure (verified). Coverage implements it app-locally
first (namespace `Coverage.ApiTokens`, designed for extraction); once stable, lift it into
e.g. `MintPlayer.Spark.Authorization.ApiTokens`:

- Token document: SHA-256(token) as document id (unique by construction), scope claims,
  created-by, optional expiry, revocation timestamp. Value = prefix + 256-bit urlsafe
  random, shown once.
- `AuthenticationHandler` resolving `Authorization: Bearer <prefix>…` / `Token <prefix>…`
  to a `ClaimsPrincipal` with scope claims; registered via the existing
  `configureProviders: Action<IdentityBuilder>` hook (SparkBuilderExtensions.cs:26-42).
- Issuance/list/revoke endpoints under `/spark/auth/tokens` (cookie-authenticated,
  XSRF-protected, IEndpointBase pattern like Logout.cs).
- Keep the scope vocabulary app-defined (library stays domain-agnostic).

Check MintPlayer/CodeCoverage for the app-local implementation to extract (M2 will add it
under `Coverage/ApiTokens/`).

## 3. Bug: external-login popup handshake never fires

- The callback only returns the `postMessage` HTML when `?popup` is on the **callback**
  (SparkAuthenticationExtensions.cs:208), but `/spark/auth/external-login` builds the
  callback URL without propagating it (:118). Demo opens the popup without `&popup=1`
  anyway (WebhooksDemo shell.component.ts:55-73) and leaks the message listener on the
  redirect path.
- Fix: propagate `popup` from external-login to the callback URL; demo: pass `popup=1`
  and remove the listener on failure paths too.
- Coverage works around it with a full-page redirect (no popup) — no urgency, but the
  feature is broken as shipped.

## 4. ng-bootstrap bump: 22.4.0 → 22.13.x

- Root package.json pins `@mintplayer/ng-bootstrap` 22.4.0; latest is 22.13.x, spanning the
  web-component rearchitecture.
- New peers to install in the workspace: `@mintplayer/web-components ^2.0.0`, `lit ^3.3.0`,
  `@mintplayer/ng-click-outside ^22.0.0`, `@mintplayer/ng-focus-on-load ^22.0.0`.
- Known breaking change hitting the demos: `<bs-accordion-tab-header>` component →
  `[bsAccordionTabHeader]` attribute directive (used in every demo shell sidebar).
  `@mintplayer/ng-swiper` was deleted upstream. Scheduler: `event-click` → `event-selected`.
- ng-spark/ng-spark-auth peer ranges (`^22.4.0` / `^22.2.0`) already admit 22.13 — this is
  about actually building/testing against it and republishing.
- Nice-to-have while there: promote WebhooksDemo's `BsShellTopbarDirective`
  (ClientApp/src/app/shell/bs-shell-topbar.directive.ts — "TODO: promote to
  @mintplayer/ng-bootstrap/shell") — though the component itself belongs in the
  ng-bootstrap repo, the demos can drop their copies after.

## 5. Decision: R4-H1 (open security finding, High)

Row-level authorization is enforced on `/spark/po` but NOT on `/spark/queries/{id}/execute`
or the WebSocket `/stream` path (docs/prd/PRD-SecurityAudit-Round4-Plan.md). Coverage
sidesteps it by running DenyAll + custom /api endpoints, but any multi-tenant Spark app
inherits it. Decide: fix in this PR (filter query/stream results through
`IsAllowedAsync` like DatabaseAccess does) or track separately.

## 6. Cheap doc fixes (opportunistic)

- README methods that don't exist: `CreateClientAsync` → `CreateInstallationClientAsync`
  (webhooks README:226,234); `UseSparkAntiforgery` (authorization README:220,284);
  `AddSparkAuthorization`/`AddSparkAuthentication`/`MapSparkIdentityApi` documented as
  public but internal.
- `AllowedDevUsers` empty-list semantics: docs say "empty = allow all", code fails closed
  (GitHubWebhooksOptions.cs:30 vs SparkBuilderExtensions.cs:101).
- `ClientSecret` documented as a webhook option but absent from `GitHubWebhooksOptions`.
- Stale queue-name tables (see item 1).
- README claims Angular 21; workspace is Angular 22.

## Verification found during out-of-tree consumption (FYI, no action yet)

- `dotnet build` of an out-of-tree app referencing the published
  `10.0.0-preview.41` packages works, including source generators and the
  Authorization package's npm auto-install of `@mintplayer/ng-spark-auth` +
  generation of `spark-auth.setup.ts`. First real PackageReference consumer = the
  MintPlayer/CodeCoverage repo.
