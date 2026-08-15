using System.Linq.Expressions;
using Coverage.Entities;
using Coverage.Indexes;
using Coverage.Services;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Queries;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;

namespace Coverage.Actions;

/// <summary>
/// A commit is visible iff its repository is. Commits carry no owner/privacy
/// fields, so the filter pushes down as an IN over the viewer's visible
/// repository ids (one memoized Raven query per request).
/// </summary>
public partial class CommitActions : DefaultPersistentObjectActions<Commit>
{
    [Inject] private readonly ISparkVisibility visibility;
    [Inject] private readonly IAsyncDocumentSession session;

    public override async Task<Expression<Func<Commit, bool>>?> GetRowFilterAsync(string action)
    {
        // Raven's .In() — see RepositoryActions.GetRowFilterAsync for why not Contains.
        var repoIds = await visibility.GetVisibleRepositoryIdsAsync();
        return c => c.Repository!.In(repoIds);
    }

    public override IReadOnlyCollection<string>? GetDefaultIncludes() => [nameof(Commit.Repository)];

    /// <summary>
    /// Custom query: commits of a repository, parent-scoped. Source:
    /// "Custom.Repository_Commits". A Custom.* source because Database.*
    /// queries drop parentId upstream (Spark#242).
    /// </summary>
    public IRavenQueryable<Commit> Repository_Commits(CustomQueryArgs args)
    {
        args.EnsureParent("Repository");
        // Through the index: its AuthoredAt field is coalesced with FirstSeenAtUtc,
        // so the query's "AuthoredAt desc" sort puts upload-only commits (which
        // never get a webhook timestamp) in chronological place instead of
        // clustering them at one end. The grid displays Commit.Date, which
        // computes the same coalesce for the viewer.
        return (IRavenQueryable<Commit>)session.Query<Commits_ByRepository.Result, Commits_ByRepository>()
            .Where(r => r.Repository == args.Parent!.Id)
            .OfType<Commit>();
    }
}
