using Coverage.Entities;
using MintPlayer.SourceGenerators.Attributes;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;

namespace Coverage.Services;

[Register(typeof(IAccountAccessService), ServiceLifetime.Scoped)]
public partial class AccountAccessService : IAccountAccessService
{
    [Inject] private readonly IUserAccessService userAccess;
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly ILogger<AccountAccessService> logger;

    /// <summary>
    /// Bounded because it feeds an all-or-nothing decision: a viewer who
    /// administers the first 1024 repositories of a larger account must not be
    /// silently promoted to whole-account authority. Hitting the cap refuses.
    /// </summary>
    private const int MaxRepositories = 1024;

    public async Task<bool> CanManageAnyRepositoryAsync(long accountGitHubId, CancellationToken cancellationToken = default)
    {
        var access = await userAccess.GetAsync(cancellationToken);
        if (access is null) return false;

        var repositories = await LoadRepositoryIdsAsync(accountGitHubId, cancellationToken);
        return repositories.Any(id => access.LevelFor(id) >= RepositoryAccessLevel.Admin);
    }

    public async Task<bool> CanAdministerWholeAccountAsync(long accountGitHubId, CancellationToken cancellationToken = default)
    {
        var access = await userAccess.GetAsync(cancellationToken);
        if (access is null) return false;

        var repositories = await LoadRepositoryIdsAsync(accountGitHubId, cancellationToken);
        if (repositories.Length == 0)
        {
            logger.LogInformation(
                "Whole-account admin refused for account {AccountGitHubId}: no known repositories to be admin of",
                accountGitHubId);
            return false;
        }
        if (repositories.Length >= MaxRepositories)
        {
            logger.LogWarning(
                "Whole-account admin refused for account {AccountGitHubId}: {Count} repositories reaches the {Cap} cap, "
                + "so 'admin on all' cannot be established",
                accountGitHubId, repositories.Length, MaxRepositories);
            return false;
        }

        var shortfall = repositories
            .Where(id => access.LevelFor(id) < RepositoryAccessLevel.Admin)
            .ToArray();

        if (shortfall.Length > 0)
        {
            // SP3: GitHub does not document whether an org owner always reports
            // permissions.admin on every repository, so a wrongly-refused owner
            // has to be diagnosable from one log line rather than a support
            // thread. If this fires for someone who genuinely owns the org, the
            // fallback is a memberships/orgs role check for this case only.
            logger.LogInformation(
                "Whole-account admin refused for account {AccountGitHubId}: not admin on {Shortfall} of {Total} repositories ({Ids})",
                accountGitHubId, shortfall.Length, repositories.Length, string.Join(", ", shortfall.Take(20)));
            return false;
        }

        return true;
    }

    private async Task<long[]> LoadRepositoryIdsAsync(long accountGitHubId, CancellationToken cancellationToken)
    {
        var ids = await session.Query<Repository, Indexes.Repositories_Overview>()
            .Where(r => r.OwnerGitHubId == accountGitHubId)
            .Select(r => r.GitHubId)
            .Take(MaxRepositories)
            .ToListAsync(cancellationToken);
        return [.. ids];
    }
}
