using Coverage.Entities;
using MintPlayer.SourceGenerators.Attributes;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;

namespace Coverage.Services;

[Register(typeof(ISparkVisibility), ServiceLifetime.Scoped)]
public partial class SparkVisibility : ISparkVisibility
{
    [Inject] private readonly IGitHubAccessService gitHubAccess;
    [Inject] private readonly IUserAccessService userAccess;
    [Inject] private readonly IAccountAccessService accountAccess;
    [Inject] private readonly IAsyncDocumentSession session;

    // Task-memoized so concurrent awaiters within the request share one computation.
    private Task<long[]>? ownerIds;
    private Task<long[]>? entitledRepositoryIds;
    private Task<string[]>? visibleRepositoryIds;

    public Task<long[]> GetAllowedOwnerIdsAsync()
        => ownerIds ??= gitHubAccess.GetAllowedOwnerIdsAsync();

    public Task<long[]> GetEntitledRepositoryGitHubIdsAsync()
        => entitledRepositoryIds ??= QueryEntitledRepositoryIdsAsync();

    private async Task<long[]> QueryEntitledRepositoryIdsAsync()
    {
        var access = await userAccess.GetAsync();
        return access is null
            ? []
            : [.. access.Repositories.Select(r => r.RepositoryGitHubId)];
    }

    public Task<string[]> GetVisibleRepositoryIdsAsync()
        => visibleRepositoryIds ??= QueryVisibleRepositoryIdsAsync();

    public async Task<bool> CanManageRepositoryAsync(long repositoryGitHubId)
    {
        var access = await userAccess.GetAsync();
        return access is not null && access.LevelFor(repositoryGitHubId) >= RepositoryAccessLevel.Admin;
    }

    public Task<bool> CanManageAccountAsync(long accountGitHubId)
        => accountAccess.CanManageAnyRepositoryAsync(accountGitHubId);

    private async Task<string[]> QueryVisibleRepositoryIdsAsync()
    {
        var entitled = await GetEntitledRepositoryGitHubIdsAsync();
        var ids = await session.Query<Repository, Indexes.Repositories_Overview>()
            .Where(RepositoryVisibility.Filter(entitled))
            .Select(r => r.Id)
            .ToListAsync();
        return [.. ids.OfType<string>()];
    }
}
