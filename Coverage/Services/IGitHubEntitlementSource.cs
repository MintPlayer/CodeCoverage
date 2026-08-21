using Coverage.Entities;

namespace Coverage.Services;

/// <summary>
/// The one place that asks GitHub what a user may reach. Behind an interface so
/// the authorization tests can pin the whole matrix without live GitHub state.
/// </summary>
public interface IGitHubEntitlementSource
{
    /// <summary>
    /// Every repository the user has explicit access to, across every
    /// installation they can reach, with the level GitHub reports.
    /// <para>
    /// One bulk pass — <c>GET /user/installations</c> then
    /// <c>GET /user/installations/{id}/repositories</c> per installation — rather
    /// than a call per repository, which is the shape that would actually
    /// hammer the user's shared 5,000/hr budget. Null means "could not ask"
    /// (GitHub unreachable, credential dead), which callers must distinguish
    /// from an empty but successful answer: failure is not absence.
    /// </para>
    /// </summary>
    Task<EntitlementSnapshot?> FetchAsync(string accessToken, CancellationToken cancellationToken = default);

    /// <summary>
    /// Live re-check of one repository as this user, for the paths where an
    /// hour-old snapshot is not good enough — currently private source content,
    /// the one irreversible disclosure. Null means "could not ask".
    /// </summary>
    Task<RepositoryAccessLevel?> FetchOneAsync(
        string accessToken, string fullName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether GitHub still considers this repository public, for refreshing the
    /// <see cref="Repository.IsPrivate"/> lease. Uses the account's installation
    /// token when there is one (authoritative, and a 5,000/hr budget) and falls
    /// back to an unauthenticated read otherwise — which is all an
    /// OIDC-provisioned repository with no installation allows.
    /// Null means "could not ask"; the caller must then keep the old value
    /// rather than guess.
    /// </summary>
    Task<bool?> IsPubliclyVisibleAsync(
        string fullName, long? installationId, CancellationToken cancellationToken = default);
}

/// <param name="OwnerGitHubIds">Accounts reachable through an installation, plus the user's own.</param>
/// <param name="Repositories">Per-repository levels; a repository absent from this list is not entitled.</param>
public sealed record EntitlementSnapshot(
    long[] OwnerGitHubIds,
    List<RepositoryEntitlement> Repositories);
