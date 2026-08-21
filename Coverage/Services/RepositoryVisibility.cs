using System.Linq.Expressions;
using Coverage.Entities;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;

namespace Coverage.Services;

/// <summary>
/// The single definition of "which repositories may this viewer see".
///
/// The rule is one line — public repositories are world-readable, private ones
/// need GitHub-granted access to the owner — and it was written three times: in
/// <c>BrowseController.ResolveVisibleRepository</c>, in
/// <see cref="SparkVisibility"/>'s id query, and in
/// <c>RepositoryActions.GetRowFilterAsync</c>. Two surfaces over the same
/// documents, two languages of expression, kept in step by a doc-comment.
/// The next visibility concept — an org allowlist, private-but-shared, an
/// unlisted state — has to land in both, and the generic /spark surface is the
/// one nobody remembers while making that change. So it lands here instead.
/// </summary>
public static class RepositoryVisibility
{
    /// <summary>
    /// The rule as a RavenDB-translatable predicate, for filtering a query.
    /// <para>
    /// <c>In()</c> rather than <c>Contains</c> is load-bearing, not style:
    /// Raven's LINQ provider fails on .NET 10 inside an <c>OrElse</c> — an
    /// array receiver binds to the untranslatable
    /// <c>MemoryExtensions.Contains</c>, and <c>List&lt;T&gt;.Contains</c>
    /// throws <c>TypedParameterExpression</c>. <c>In()</c> also has a real
    /// in-memory implementation, which Spark's compiled single-row checks rely
    /// on when they evaluate this same expression against one loaded document.
    /// </para>
    /// <para>
    /// The key is the **repository's** numeric GitHub id, taken from the viewer's
    /// persisted entitlement snapshot. Owner-granularity was the original defect:
    /// reaching an organization's installation granted every repository that
    /// organization owns, so an outside collaborator on one public repository
    /// could read every private sibling. GitHub answers per repository, and now so
    /// does this. Ids rather than logins because a login is mutable — an org
    /// rename would otherwise detach every repository from its viewers, and a
    /// freed login taken over by another party would grant its old access.
    /// </para>
    /// </summary>
    public static Expression<Func<Repository, bool>> Filter(long[] entitledRepositoryGitHubIds)
        => repository => !repository.IsPrivate || repository.GitHubId.In(entitledRepositoryGitHubIds);

    /// <summary>
    /// The same rule for one already-loaded repository, so an imperative caller
    /// cannot drift from the query one.
    /// </summary>
    public static bool IsVisible(Repository repository, long[] entitledRepositoryGitHubIds)
        => !repository.IsPrivate || entitledRepositoryGitHubIds.Contains(repository.GitHubId);
}
