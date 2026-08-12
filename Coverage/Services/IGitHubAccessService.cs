namespace Coverage.Services;

/// <summary>
/// Mirrors the signed-in user's GitHub visibility: which owners (their own login
/// plus every org/user whose app installation they can access) they may see.
/// GitHub is the authority — there is no app-local ACL.
/// </summary>
public interface IGitHubAccessService
{
    Task<string[]> GetAllowedOwnersAsync(CancellationToken cancellationToken = default);
    Task<bool> IsOwnerAllowedAsync(string ownerLogin, CancellationToken cancellationToken = default);

    /// <summary>Drops the current user's cached owner list so the next call re-queries GitHub (manual resync).</summary>
    Task InvalidateAsync(CancellationToken cancellationToken = default);
}
