using System.Linq.Expressions;
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
/// Row security for the generic /spark read surface — same semantics as
/// BrowseController.ResolveVisibleRepository: anonymous viewers see public
/// repositories; authenticated viewers additionally the repos of owners GitHub
/// grants them. Writes are never granted in security.json, so the WITH CHECK
/// path is unreachable and no machine-principal branch is needed.
/// </summary>
public partial class RepositoryActions : DefaultPersistentObjectActions<Repository>
{
    [Inject] private readonly ISparkVisibility visibility;
    [Inject] private readonly IAsyncDocumentSession session;

    public override async Task<Expression<Func<Repository, bool>>?> GetRowFilterAsync(string action)
    {
        // One visibility rule for every action: writes are denied at the type
        // level (no Edit/New/Delete right in security.json), and since Spark#244
        // the per-row `can` block intersects type-level rights, so no
        // write-action special-casing is needed here.
        // Empty for anonymous viewers → the filter reduces to "public only".
        var entitled = await visibility.GetEntitledRepositoryGitHubIdsAsync();
        return RepositoryVisibility.Filter(entitled);
    }

    /// <summary>
    /// BadgeToken grants badge access on private repos, so it is a credential and
    /// goes to repository administrators only — not to everyone who can read the
    /// repository, and not to everyone who can reach the owner's installation.
    /// </summary>
    /// <summary>
    /// BadgeToken is a credential, however narrow. Gate is the owner's CI policy —
    /// thresholds, targets, whether it blocks; the /api route for it already
    /// required ownership, while the generic detail PO handed it to anyone who
    /// could read the repository, which for a public repository is everyone.
    /// </summary>
    public static readonly string[] AdministratorOnlyAttributes =
        [nameof(Repository.BadgeToken), nameof(Repository.Gate)];

    public override async Task<IReadOnlyCollection<string>?> GetProtectedAttributesAsync(string action, Repository entity)
        => await visibility.CanManageRepositoryAsync(entity.GitHubId) ? null : AdministratorOnlyAttributes;

    public override IReadOnlyCollection<string>? GetDefaultIncludes() => [nameof(Repository.Account)];

    /// <summary>
    /// Custom query: repositories of an account, parent-scoped. Source:
    /// "Custom.Account_Repositories". A Custom.* source because Database.*
    /// queries drop parentId upstream (Spark#242); the framework still applies
    /// the row filter and sorting on top.
    /// </summary>
    public IRavenQueryable<Repository> Account_Repositories(CustomQueryArgs args)
    {
        args.EnsureParent("Account");
        return session.Query<Repository, Indexes.Repositories_Overview>()
            .Where(r => r.Account == args.Parent!.Id);
    }
}
