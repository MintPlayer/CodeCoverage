using Coverage.Entities;
using Microsoft.AspNetCore.Identity;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Authorization.Identity;
using Raven.Client.Documents.Session;

namespace Coverage.Services;

/// <summary>
/// The persisted-entitlement half of the design: derive from GitHub, store
/// locally, invalidate on webhooks.
/// </summary>
[Register(typeof(IUserAccessService), ServiceLifetime.Scoped)]
public partial class UserAccessService : IUserAccessService
{
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;
    [Inject] private readonly UserManager<SparkUser> userManager;
    [Inject] private readonly IGitHubUserTokenService tokenService;
    [Inject] private readonly IGitHubEntitlementSource entitlementSource;
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly ILogger<UserAccessService> logger;

    /// <summary>
    /// The backstop, not the primary bound. With the membership webhooks
    /// subscribed, org, collaborator and team changes invalidate within seconds;
    /// this only has to cover a dropped message and an installation whose org has
    /// not accepted the Members permission. It must never be infinite — a
    /// dropped invalidation with no TTL is permanent.
    /// </summary>
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(60);

    // One rebuild per request at most, however many gates ask.
    private Task<UserAccess?>? snapshot;

    public Task<UserAccess?> GetAsync(CancellationToken cancellationToken = default)
        => snapshot ??= ResolveAsync(cancellationToken);

    private async Task<UserAccess?> ResolveAsync(CancellationToken cancellationToken)
    {
        var principal = httpContextAccessor.HttpContext?.User;
        if (principal?.Identity?.IsAuthenticated != true)
            return null;

        var user = await userManager.GetUserAsync(principal);
        if (user is null)
            return null;

        var documentId = UserAccess.DocumentId(user.Id!);
        var existing = await session.LoadAsync<UserAccess>(documentId, cancellationToken);

        var staleness = await DescribeStalenessAsync(existing, cancellationToken);
        if (staleness is null)
        {
            logger.LogDebug("UserAccess {DocumentId}: fresh ({Count} repositories)",
                documentId, existing!.Repositories.Count);
            return existing;
        }

        logger.LogInformation("UserAccess {DocumentId}: rebuilding — {Reason}", documentId, staleness);

        var token = await tokenService.GetAccessTokenAsync(user, forceRefresh: false, cancellationToken);
        if (token.State != GitHubTokenState.Ok || token.AccessToken is null)
        {
            // No credential to ask with. An existing snapshot, even expired, is a
            // better answer than pretending the user lost all access — and it is
            // still bounded, because the next successful request rebuilds it.
            logger.LogWarning("UserAccess {DocumentId}: cannot rebuild ({State}); serving the stale snapshot",
                documentId, token.State);
            return existing;
        }

        var fetched = await entitlementSource.FetchAsync(token.AccessToken, cancellationToken);
        if (fetched is null)
        {
            logger.LogWarning("UserAccess {DocumentId}: GitHub did not answer; serving the stale snapshot", documentId);
            return existing;
        }

        return await StoreAsync(existing, documentId, user.Id!, fetched, cancellationToken);
    }

    /// <summary>
    /// Why the snapshot needs rebuilding, or null when it does not. A string
    /// rather than a bool so the reason reaches the log — "why did this user just
    /// cost a GitHub round-trip" is otherwise unanswerable.
    /// </summary>
    private async Task<string?> DescribeStalenessAsync(UserAccess? existing, CancellationToken cancellationToken)
    {
        if (existing is null) return "no snapshot yet";

        var age = DateTime.UtcNow - existing.BuiltAtUtc;
        if (age >= Ttl) return $"age {age:hh\\:mm} exceeds the {Ttl:hh\\:mm} TTL";

        if (existing.BuiltAgainstEpochs.Count == 0) return null;

        var accounts = await session.LoadAsync<Account>(existing.BuiltAgainstEpochs.Keys, cancellationToken);
        foreach (var (accountId, builtAgainst) in existing.BuiltAgainstEpochs)
        {
            var account = accounts.GetValueOrDefault(accountId);
            if (account is not null && account.AccessEpoch != builtAgainst)
                return $"{accountId} moved from epoch {builtAgainst} to {account.AccessEpoch}";
        }

        return null;
    }

    private async Task<UserAccess> StoreAsync(
        UserAccess? existing, string documentId, string userId,
        EntitlementSnapshot fetched, CancellationToken cancellationToken)
    {
        var access = existing;
        if (access is null)
        {
            access = new UserAccess { UserId = userId };
            await session.StoreAsync(access, documentId, cancellationToken);
        }

        access.OwnerGitHubIds = fetched.OwnerGitHubIds;
        access.Repositories = fetched.Repositories;
        access.BuiltAtUtc = DateTime.UtcNow;

        // Stamp the epochs *after* fetching, so a bump that lands mid-fetch leaves
        // the snapshot looking stale rather than falsely current.
        var accountIds = fetched.OwnerGitHubIds.Select(Account.DocumentId).ToArray();
        var accounts = await session.LoadAsync<Account>(accountIds, cancellationToken);
        access.BuiltAgainstEpochs = accountIds.ToDictionary(
            id => id,
            id => accounts.GetValueOrDefault(id)?.AccessEpoch ?? 0);

        await session.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "UserAccess {DocumentId}: rebuilt — {Owners} owners, {Repositories} repositories ({Admin} admin)",
            documentId, access.OwnerGitHubIds.Length, access.Repositories.Count,
            access.Repositories.Count(r => r.Level == RepositoryAccessLevel.Admin));

        return access;
    }

    public async Task InvalidateAsync(CancellationToken cancellationToken = default)
    {
        var principal = httpContextAccessor.HttpContext?.User;
        if (principal?.Identity?.IsAuthenticated != true)
            return;

        var user = await userManager.GetUserAsync(principal);
        if (user is null) return;

        var documentId = UserAccess.DocumentId(user.Id!);
        var existing = await session.LoadAsync<UserAccess>(documentId, cancellationToken);
        if (existing is not null)
        {
            // Delete rather than patch: GitHub stays the sole writer, so a
            // rebuild is the only way the snapshot ever gains content.
            session.Delete(existing);
            await session.SaveChangesAsync(cancellationToken);
            logger.LogInformation("UserAccess {DocumentId}: invalidated by request", documentId);
        }

        snapshot = null;
    }
}
