using Coverage.Entities;
using Coverage.Services;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Queries;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;

namespace Coverage.Actions;

/// <summary>
/// A build is visible iff its commit's repository is. Builds carry no owner
/// fields; the repo is recovered from the Commit reference's id shape
/// (Commits/{repoGitHubId}/{sha}) — not expressible as a pushdown expression,
/// so this stays the per-row predicate. The only I/O behind it is the
/// per-request memoized repo-id list, so per-row evaluation is in-memory.
/// </summary>
public partial class BuildActions : DefaultPersistentObjectActions<Build>
{
    [Inject] private readonly ISparkVisibility visibility;
    [Inject] private readonly IAsyncDocumentSession session;

    public override async Task<bool> IsAllowedAsync(string action, Build entity)
    {
        if (RepositoryGitHubIdFromCommitId(entity.Commit) is not { } repositoryGitHubId)
            return false;
        var visible = await visibility.GetVisibleRepositoryIdsAsync();
        return visible.Contains(Repository.DocumentId(repositoryGitHubId), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The owning repository's GitHub id, recovered from the commit reference's id
    /// shape (<c>Commits/{repoGitHubId}/{sha}</c>). Builds carry no owner field,
    /// and this is not expressible as a pushdown expression — hence the per-row
    /// predicate above.
    /// </summary>
    private static long? RepositoryGitHubIdFromCommitId(string? commitId)
    {
        var parts = commitId?.Split('/');
        return parts is { Length: >= 3 } && long.TryParse(parts[1], out var repositoryGitHubId)
            ? repositoryGitHubId
            : null;
    }

    /// <summary>
    /// CI internals are not coverage data. A build carries the runner's absolute
    /// workspace path, the uploaded file names, the job name, parse error text,
    /// the gate snapshot and check-run ids — all of which reached anonymous
    /// callers, on the grid as well as the detail view, because <c>Sessions</c> is
    /// a query column. None of it is needed to read a coverage percentage, and it
    /// describes someone else's build environment.
    ///
    /// Dotted names recurse into the embedded session rows, which is the only way
    /// to reach fields a row filter cannot see.
    /// </summary>
    public override async Task<IReadOnlyCollection<string>?> GetProtectedAttributesAsync(string action, Build entity)
    {
        if (RepositoryGitHubIdFromCommitId(entity.Commit) is { } repositoryGitHubId
            && await visibility.CanManageRepositoryAsync(repositoryGitHubId))
        {
            return null;
        }

        return AdministratorOnlyAttributes;
    }

    /// <summary>
    /// Dotted names recurse into the embedded session rows, which is the only way
    /// to reach fields a row filter cannot see.
    /// </summary>
    public static readonly string[] AdministratorOnlyAttributes =
    [
        $"{nameof(Build.Sessions)}.{nameof(BuildSession.RootDir)}",
        $"{nameof(Build.Sessions)}.{nameof(BuildSession.RawFileNames)}",
        $"{nameof(Build.Sessions)}.{nameof(BuildSession.JobName)}",
        $"{nameof(Build.Sessions)}.{nameof(BuildSession.Error)}",
        nameof(Build.GateSnapshot),
        nameof(Build.Feedback),
        nameof(Build.DeclaredBaseSha),
    ];

    public override IReadOnlyCollection<string>? GetDefaultIncludes() => [nameof(Build.Commit)];

    /// <summary>
    /// Custom query: builds of a commit, parent-scoped. Source: "Custom.Commit_Builds".
    /// A Custom.* source because Database.* queries drop parentId upstream (Spark#242).
    /// Build.Commit holds the exact commit document id, so equality suffices (no
    /// prefix filtering — FileCoverage/BuildTreeSummary share the id prefix but are
    /// different collections and never enter this query).
    /// </summary>
    public IRavenQueryable<Build> Commit_Builds(CustomQueryArgs args)
    {
        args.EnsureParent("Commit");
        return session.Query<Build, Indexes.Builds_Overview>()
            .Where(b => b.Commit == args.Parent!.Id);
    }
}
