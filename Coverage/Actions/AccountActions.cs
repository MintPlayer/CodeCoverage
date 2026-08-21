using Coverage.Entities;
using Coverage.Services;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Actions;

namespace Coverage.Actions;

/// <summary>
/// Accounts are public data (GitHub logins/avatars), so there is no row filter —
/// but the installation id is operational detail, and "manager" now means someone
/// who actually administers a repository here rather than anyone who can reach
/// the installation.
/// </summary>
public partial class AccountActions : DefaultPersistentObjectActions<Account>
{
    [Inject] private readonly ISparkVisibility visibility;

    public override async Task<IReadOnlyCollection<string>?> GetProtectedAttributesAsync(string action, Account entity)
        => await visibility.CanManageAccountAsync(entity.GitHubId)
            ? null
            : [nameof(Account.InstallationId)];
}
