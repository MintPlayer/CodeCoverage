using System.Linq.Expressions;
using Coverage.Entities;
using Coverage.Indexes;
using Coverage.Services;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Queries;
using Raven.Client.Documents;
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
    public async Task<IQueryable<Commit>> Repository_Commits(CustomQueryArgs args)
    {
        args.EnsureParent("Repository");

        // Ordered through the index, whose AuthoredAt field is coalesced with
        // FirstSeenAtUtc — upload-only commits (no webhook timestamp) land in
        // chronological place instead of clustering at one end.
        //
        // The query also declares sortColumns: Date desc, which until Spark
        // 10.0.0-preview.59 was silently ignored for an async custom query and now
        // takes effect. Both survive because Commit.Date is the same coalescing
        // (AuthoredAt ?? FirstSeenAtUtc), so the framework re-sorts the same order
        // in memory — but that is a coincidence, not a design. This ordering is
        // the one that matters: it runs server-side on an index term, before
        // materialization, so it is what makes the query bounded rather than
        // "fetch everything, then sort".
        var commits = await session.Query<Commits_ByRepository.Result, Commits_ByRepository>()
            .Customize(x => x.NoTracking())
            .Where(r => r.Repository == args.Parent!.Id)
            .OrderByDescending(r => r.AuthoredAt)
            .OfType<Commit>()
            .ToListAsync();

        StampCoverageDeltas(commits);
        // In-memory from here: the framework materializes every custom query in
        // full before paging anyway, so this costs nothing extra — and it is what
        // lets the declared sortColumns name a computed property (Date), which a
        // Raven queryable could not translate. If this ever returns a Raven
        // queryable again, that sort must move to an indexed field.
        return commits.AsQueryable();
    }

    /// <summary>
    /// Fills in each commit's coverage change versus the chronologically next
    /// one in the sequence — the "Δ" column. Anchored to the order computed
    /// here, so re-sorting or paging the grid can't change what a row's number
    /// means. Commits without coverage on either side get no delta rather than
    /// a fake zero.
    /// </summary>
    private static void StampCoverageDeltas(IReadOnlyList<Commit> newestFirst)
    {
        for (var i = 0; i < newestFirst.Count - 1; i++)
        {
            var current = Percent(newestFirst[i].Coverage);
            var previous = Percent(newestFirst[i + 1].Coverage);
            if (current is not null && previous is not null)
                newestFirst[i].CoverageDelta = current - previous;
        }
    }

    private static double? Percent(CoverageSummary? summary)
        => summary is { LinesCoverable: > 0 }
            ? summary.LinesCovered * 100d / summary.LinesCoverable
            : null;
}
