namespace Coverage.Services;

/// <summary>
/// Management rights at the *account* granularity, for the two questions a
/// per-repository level cannot answer on its own.
///
/// This is the split that closes the original defect: management used to require
/// only that a viewer could reach the installation, which is owner-granular — so
/// any org member, including one with read-only access to a single repository,
/// could mint an account-scoped upload credential covering the whole
/// organization and revoke everyone else's.
/// </summary>
public interface IAccountAccessService
{
    /// <summary>
    /// Whether the viewer administers at least one repository of this account.
    /// The bar for *seeing* account-level operational detail (the installation
    /// id) and for listing the account's tokens — enough to prove they are a
    /// maintainer here, without claiming authority over the whole account.
    /// </summary>
    Task<bool> CanManageAnyRepositoryAsync(long accountGitHubId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether the viewer administers **every** repository we know of for this
    /// account. The bar for minting or revoking an account-scoped credential,
    /// which by definition can upload for all of them.
    /// <para>
    /// An account with no known repositories answers false: <c>All()</c> over an
    /// empty set is vacuously true, and that would hand whole-account authority
    /// to anyone at all.
    /// </para>
    /// </summary>
    Task<bool> CanAdministerWholeAccountAsync(long accountGitHubId, CancellationToken cancellationToken = default);
}
