using System.Linq.Expressions;
using Coverage.Entities;
using Coverage.Services;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Actions;
using Raven.Client.Documents.Linq;

namespace Coverage.Actions;

/// <summary>
/// A commit is visible iff its repository is. Commits carry no owner/privacy
/// fields, so the filter pushes down as an IN over the viewer's visible
/// repository ids (one memoized Raven query per request).
/// </summary>
public partial class CommitActions : DefaultPersistentObjectActions<Commit>
{
    [Inject] private readonly ISparkVisibility visibility;

    public override async Task<Expression<Func<Commit, bool>>?> GetRowFilterAsync(string action)
    {
        // Read-only surface (see RepositoryActions for the Spark#243 note).
        if (action is "Edit" or "Delete" or "New")
            return c => false;

        // Raven's .In() — see RepositoryActions.GetRowFilterAsync for why not Contains.
        var repoIds = await visibility.GetVisibleRepositoryIdsAsync();
        return c => c.Repository!.In(repoIds);
    }

    public override IReadOnlyCollection<string>? GetDefaultIncludes() => [nameof(Commit.Repository)];
}
