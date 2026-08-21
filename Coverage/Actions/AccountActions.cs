using System.Linq.Expressions;
using Coverage.Entities;
using Coverage.Services;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Actions;

namespace Coverage.Actions;

/// <summary>
/// An account is anonymously visible only once it owns something anonymously
/// visible (<see cref="AccountVisibility"/>), and the installation id is
/// operational detail on top of that — where "manager" means someone who
/// actually administers a repository here, not anyone who can reach the
/// installation.
/// </summary>
public partial class AccountActions : DefaultPersistentObjectActions<Account>
{
    [Inject] private readonly ISparkVisibility visibility;

    /// <summary>
    /// Withheld from anyone who administers nothing in this account. Exposed as a
    /// constant so the anonymous-surface matrix can assert the set without
    /// standing up the Spark action pipeline.
    /// </summary>
    public static readonly string[] ManagerOnlyAttributes = [nameof(Account.InstallationId)];

    public override async Task<Expression<Func<Account, bool>>?> GetRowFilterAsync(string action)
        => AccountVisibility.Filter(await visibility.GetAllowedOwnerIdsAsync());

    public override async Task<IReadOnlyCollection<string>?> GetProtectedAttributesAsync(string action, Account entity)
        => await visibility.CanManageAccountAsync(entity.GitHubId) ? null : ManagerOnlyAttributes;
}
