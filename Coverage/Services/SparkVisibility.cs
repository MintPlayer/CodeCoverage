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
    private Task<string[]>? owners;
    private Task<string[]>? visibleRepositoryIds;

    public Task<string[]> GetAllowedOwnersAsync()
        => owners ??= gitHubAccess.GetAllowedOwnersAsync();

    public Task<string[]> GetVisibleRepositoryIdsAsync()
        => visibleRepositoryIds ??= QueryVisibleRepositoryIdsAsync();

    public async Task<bool> CanManageOwnerAsync(string ownerLogin)
        => (await GetAllowedOwnersAsync()).Contains(ownerLogin, StringComparer.OrdinalIgnoreCase);

    private async Task<string[]> QueryVisibleRepositoryIdsAsync()
    {
        // Raven's .In() is the one list-membership shape its LINQ provider
        // reliably translates inside an OrElse. Plain Contains fails twice on
        // .NET 10: a string[] receiver binds to untranslatable
        // MemoryExtensions.Contains, and even List<string>.Contains inside
        // "!x || list.Contains(y)" throws TypedParameterExpression.
        var allowed = await GetAllowedOwnersAsync();
        var ids = await session.Query<Repository>()
            .Where(r => !r.IsPrivate || r.OwnerLogin.In(allowed))
            .Select(r => r.Id)
            .ToListAsync();
        return [.. ids.OfType<string>()];
    }
}
