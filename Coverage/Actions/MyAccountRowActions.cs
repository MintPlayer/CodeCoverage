using Coverage.Entities;
using Coverage.Services;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Queries;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;

namespace Coverage.Actions;

/// <summary>
/// The home page's "your accounts" grid.
///
/// No row filter and no redaction hook, deliberately: every row is built from the
/// caller's own entitlement snapshot, so the query cannot return a row the caller
/// is not entitled to. The type-level right is scoped to the <c>Authenticated</c>
/// group in <c>security.json</c> rather than <c>Everyone</c>, so an anonymous
/// caller is refused before any of this runs.
/// </summary>
public partial class MyAccountRowActions : DefaultPersistentObjectActions<MyAccountRow>
{
    [Inject] private readonly ISparkVisibility visibility;
    [Inject] private readonly IAsyncDocumentSession session;

    /// <summary>Matches the cap the home endpoint used; an owner list longer than this has other problems.</summary>
    private const int MaxRepositories = 4096;

    /// <summary>
    /// Custom query: the viewer's own accounts. Source <c>Custom.My_Accounts</c>,
    /// declared parentless — the home page has no parent persistent object, and
    /// Spark skips parent resolution entirely when parentId/parentType are empty.
    ///
    /// <para>
    /// Rows are computed in memory and returned as <c>.AsQueryable()</c>. Since
    /// Spark 10.0.0-preview.59 (issue #294) queryable capabilities are inferred
    /// from the runtime result rather than the declared return type, so an async
    /// method keeps declared <c>sortColumns</c> and header-click sorting — which
    /// is why there is no ordering here.
    /// </para>
    /// </summary>
    public async Task<IQueryable<MyAccountRow>> My_Accounts(CustomQueryArgs args)
    {
        var owners = await visibility.GetAllowedOwnerIdsAsync();
        if (owners.Length == 0)
            return Array.Empty<MyAccountRow>().AsQueryable();

        var known = await session.Query<Account, Indexes.Accounts_Overview>()
            .Where(a => a.GitHubId.In(owners))
            .ToListAsync();
        var byGitHubId = known.ToDictionary(a => a.GitHubId);

        // Every repository of every reachable owner, then narrowed to the ones this
        // viewer may actually see. Counting all of an owner's repositories would
        // report — and aggregate the coverage of — private repositories the viewer
        // has no entitlement to, which is exactly what per-repository entitlement
        // exists to prevent. The rule comes from RepositoryVisibility so there is
        // still one definition of it, shared with the row filters.
        var entitled = await visibility.GetEntitledRepositoryGitHubIdsAsync();
        var repositories = await session.Query<Repository, Indexes.Repositories_Overview>()
            .Where(r => r.OwnerGitHubId.In(owners))
            .Take(MaxRepositories)
            .ToListAsync();
        var visibleByOwner = repositories
            .Where(r => RepositoryVisibility.IsVisible(r, entitled))
            .ToLookup(r => r.OwnerGitHubId);

        return owners
            .Select(owner => Row(owner, byGitHubId.GetValueOrDefault(owner), [.. visibleByOwner[owner]]))
            .AsQueryable();
    }

    /// <summary>
    /// One row. An owner GitHub reports an installation for but we hold no
    /// <c>Account</c> document for still gets a row — that gap exists because the
    /// backfill runs on a later request — and it needs a distinct
    /// <see cref="MyAccountRow.Id"/>, because Spark finishes with
    /// <c>DistinctBy(po =&gt; po.Id)</c> and rows sharing a null id collapse into one.
    /// </summary>
    private static MyAccountRow Row(long ownerGitHubId, Account? known, Repository[] visible)
    {
        var covered = visible.Sum(r => r.LatestCoverage?.LinesCovered ?? 0);
        var coverable = visible.Sum(r => r.LatestCoverage?.LinesCoverable ?? 0);

        return new MyAccountRow
        {
            Id = Account.DocumentId(ownerGitHubId),
            GitHubId = ownerGitHubId,
            // The login is unknown until the backfill creates the document; the
            // repositories we can see are the only other place it appears.
            Login = known?.Login ?? visible.FirstOrDefault()?.OwnerLogin ?? ownerGitHubId.ToString(),
            Type = known?.Type ?? "User",
            AvatarUrl = known?.AvatarUrl,
            IsAppInstalled = known?.InstallationId is not null,
            RepositoryCount = visible.Length,
            AggregateCoverage = coverable > 0 ? Math.Round(covered * 100.0 / coverable, 1) : null,
        };
    }
}
