using System.Linq.Expressions;
using Coverage.Entities;
using Coverage.Services;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Actions;

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
        var repoIds = await visibility.GetVisibleRepositoryIdsAsync();
        return c => repoIds.Contains(c.Repository!);
    }

    public override IReadOnlyCollection<string>? GetDefaultIncludes() => [nameof(Commit.Repository)];
}
