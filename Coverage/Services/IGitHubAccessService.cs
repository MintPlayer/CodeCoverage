namespace Coverage.Services;

/// <summary>
/// Mirrors the signed-in user's GitHub visibility: which owners (their own
/// account plus every org/user whose app installation they can access) they may
/// see. GitHub is the authority — there is no app-local ACL.
///
/// Owners are identified by their **numeric GitHub id**, never their login: a
/// login is mutable, so an org rename would silently detach every repository
/// from its viewers, and a freed login taken over by another party would let a
/// stale local account authorize as the new owner.
/// </summary>
public interface IGitHubAccessService
{
    /// <summary>
    /// The owners the current user may see, plus the health of their GitHub
    /// credential. When the token is dead (<see cref="GitHubTokenState.ReauthRequired"/>)
    /// or GitHub is unreachable (<see cref="GitHubTokenState.Unavailable"/>),
    /// the list degrades to the user's own account — failure is not absence.
    /// </summary>
    Task<GitHubVisibility> GetVisibilityAsync(CancellationToken cancellationToken = default);

    Task<long[]> GetAllowedOwnerIdsAsync(CancellationToken cancellationToken = default);
    Task<bool> IsOwnerAllowedAsync(long ownerGitHubId, CancellationToken cancellationToken = default);

    /// <summary>Drops the current user's cached owner list so the next call re-queries GitHub (manual resync).</summary>
    Task InvalidateAsync(CancellationToken cancellationToken = default);
}

/// <param name="OwnerGitHubIds">Numeric GitHub ids of the accounts this viewer may see.</param>
public sealed record GitHubVisibility(long[] OwnerGitHubIds, GitHubTokenState TokenState);
