using Coverage.Entities;
using Microsoft.AspNetCore.Identity;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Authorization.Identity;
using Raven.Client.Documents.Session;

namespace Coverage.Services;

[Register(typeof(IRepositoryAccessService), ServiceLifetime.Scoped)]
public partial class RepositoryAccessService : IRepositoryAccessService
{
    [Inject] private readonly IUserAccessService userAccess;
    [Inject] private readonly IGitHubEntitlementSource entitlementSource;
    [Inject] private readonly IGitHubUserTokenService tokenService;
    [Inject] private readonly UserManager<SparkUser> userManager;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;
    [Inject] private readonly IAccountPublicityService publicity;
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly ILogger<RepositoryAccessService> logger;

    /// <summary>
    /// How long a confirmed public/private answer is trusted. Long, because it is
    /// shared by every viewer of that repository and the unauthenticated fallback
    /// is metered at 60/hr per IP; short enough that a repository made private
    /// stops leaking the same working day.
    /// </summary>
    private static readonly TimeSpan VisibilityLease = TimeSpan.FromHours(6);

    // The lease is per repository and shared by every viewer, so one refresh per
    // repository per request is plenty however many gates ask.
    private readonly Dictionary<long, Task> refreshes = [];

    public async Task<RepositoryAccessLevel> GetAsync(Repository repository, CancellationToken cancellationToken = default)
    {
        await EnsureVisibilityFreshAsync(repository, cancellationToken);

        if (!repository.IsPrivate)
            return RepositoryAccessLevel.Read;

        var access = await userAccess.GetAsync(cancellationToken);
        if (access is null)
        {
            logger.LogDebug("Access check: {FullName} is private and the caller has no snapshot — denied", repository.FullName);
            return RepositoryAccessLevel.None;
        }

        var level = access.LevelFor(repository.GitHubId);
        logger.LogDebug("Access check: {FullName} (private) resolves to {Level} for user {UserId}",
            repository.FullName, level, access.UserId);
        return level;
    }

    public async Task<RepositoryAccessLevel> GetVerifiedAsync(Repository repository, CancellationToken cancellationToken = default)
    {
        await EnsureVisibilityFreshAsync(repository, cancellationToken);

        if (!repository.IsPrivate)
            return RepositoryAccessLevel.Read;

        var snapshotLevel = await GetAsync(repository, cancellationToken);

        var token = await CurrentUserTokenAsync(cancellationToken);
        if (token is null)
        {
            logger.LogDebug("Verified access check: {FullName} has no usable credential; falling back to the snapshot ({Level})",
                repository.FullName, snapshotLevel);
            return snapshotLevel;
        }

        var live = await entitlementSource.FetchOneAsync(token, repository.FullName, cancellationToken);
        if (live is null)
        {
            // GitHub could not answer. The snapshot is bounded by its own TTL, so
            // trusting it here is the same exposure every other path already has —
            // and refusing would make the file view fail whenever GitHub hiccups.
            logger.LogWarning("Verified access check: GitHub did not answer for {FullName}; falling back to the snapshot ({Level})",
                repository.FullName, snapshotLevel);
            return snapshotLevel;
        }

        if (live != snapshotLevel)
        {
            logger.LogInformation(
                "Verified access check: {FullName} live={Live} disagrees with snapshot={Snapshot} — the snapshot is stale",
                repository.FullName, live, snapshotLevel);
        }

        // The live answer wins in both directions: it revokes a stale grant, and
        // it grants access the snapshot has not caught up with yet.
        return live.Value;
    }

    /// <summary>
    /// Renews the public/private lease when it has expired. Never widens access
    /// on a failure to ask: an unanswerable check leaves the stored value alone.
    /// </summary>
    private Task EnsureVisibilityFreshAsync(Repository repository, CancellationToken cancellationToken)
    {
        if (repository.VisibilityCheckedAtUtc is { } checkedAt
            && DateTime.UtcNow - checkedAt < VisibilityLease)
            return Task.CompletedTask;

        if (refreshes.TryGetValue(repository.GitHubId, out var inFlight))
            return inFlight;

        var refresh = RefreshVisibilityAsync(repository, cancellationToken);
        refreshes[repository.GitHubId] = refresh;
        return refresh;
    }

    private async Task RefreshVisibilityAsync(Repository repository, CancellationToken cancellationToken)
    {
        var installationId = await ResolveInstallationIdAsync(repository, cancellationToken);

        var publiclyVisible = await entitlementSource.IsPubliclyVisibleAsync(
            repository.FullName, installationId, cancellationToken);
        if (publiclyVisible is null)
        {
            logger.LogWarning("Visibility lease for {FullName} could not be renewed; keeping IsPrivate={IsPrivate}",
                repository.FullName, repository.IsPrivate);
            return;
        }

        var becamePrivate = publiclyVisible.Value is false && !repository.IsPrivate;
        if (becamePrivate)
        {
            logger.LogWarning(
                "Visibility lease for {FullName}: GitHub no longer serves it publicly — flipping to private. "
                + "Its badge will render 'unknown' until a badge token is minted.",
                repository.FullName);
        }

        var delta = IAccountPublicityService.DeltaFor(repository.IsPrivate, !publiclyVisible.Value);
        repository.IsPrivate = !publiclyVisible.Value;
        repository.VisibilityCheckedAtUtc = DateTime.UtcNow;

        if (delta != 0)
        {
            var account = await session.LoadAsync<Account>(
                Account.DocumentId(repository.OwnerGitHubId), cancellationToken);
            if (account is not null) publicity.Adjust(account, delta);
        }

        await session.SaveChangesAsync(cancellationToken);
    }

    private async Task<long?> ResolveInstallationIdAsync(Repository repository, CancellationToken cancellationToken)
    {
        if (repository.Account is null) return null;
        var account = await session.LoadAsync<Account>(repository.Account, cancellationToken);
        return account?.InstallationId;
    }

    private async Task<string?> CurrentUserTokenAsync(CancellationToken cancellationToken)
    {
        var principal = httpContextAccessor.HttpContext?.User;
        if (principal?.Identity?.IsAuthenticated != true) return null;

        var user = await userManager.GetUserAsync(principal);
        if (user is null) return null;

        var token = await tokenService.GetAccessTokenAsync(user, forceRefresh: false, cancellationToken);
        return token.State == GitHubTokenState.Ok ? token.AccessToken : null;
    }
}
