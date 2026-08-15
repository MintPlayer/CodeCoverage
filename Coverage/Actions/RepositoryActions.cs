using System.Linq.Expressions;
using Coverage.Entities;
using Coverage.Services;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Queries;
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
        // Empty for anonymous viewers → the filter reduces to "public only".
        var owners = await visibility.GetAllowedOwnersAsync();
        return r => !r.IsPrivate || owners.Contains(r.OwnerLogin);
    }

    /// <summary>BadgeToken grants badge access on private repos — managers only.</summary>
    public override async Task<IReadOnlyCollection<string>?> GetProtectedAttributesAsync(string action, Repository entity)
        => await visibility.CanManageOwnerAsync(entity.OwnerLogin)
            ? null
            : [nameof(Repository.BadgeToken)];

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
        return session.Query<Repository>().Where(r => r.Account == args.Parent!.Id);
    }
}
