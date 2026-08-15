using System.Linq.Expressions;
using Coverage.Entities;
using Coverage.Services;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Actions;

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
}
