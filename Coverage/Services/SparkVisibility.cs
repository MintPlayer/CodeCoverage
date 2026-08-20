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
    [Inject] private readonly IAsyncDocumentSession session;

    // Task-memoized so concurrent awaiters within the request share one computation.
    private Task<long[]>? ownerIds;
    private Task<string[]>? visibleRepositoryIds;

    public Task<long[]> GetAllowedOwnerIdsAsync()
        => ownerIds ??= gitHubAccess.GetAllowedOwnerIdsAsync();

    public Task<string[]> GetVisibleRepositoryIdsAsync()
        => visibleRepositoryIds ??= QueryVisibleRepositoryIdsAsync();

    public async Task<bool> CanManageOwnerAsync(long ownerGitHubId)
        => (await GetAllowedOwnerIdsAsync()).Contains(ownerGitHubId);

    private async Task<string[]> QueryVisibleRepositoryIdsAsync()
    {
        var allowed = await GetAllowedOwnerIdsAsync();
        var ids = await session.Query<Repository, Indexes.Repositories_Overview>()
            .Where(RepositoryVisibility.Filter(allowed))
            .Select(r => r.Id)
            .ToListAsync();
        return [.. ids.OfType<string>()];
    }
}
