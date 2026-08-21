using Coverage.Entities;

namespace Coverage.Services;

/// <summary>
/// Loads — and rebuilds when stale — the signed-in user's persisted entitlement
/// snapshot. Serving costs one RavenDB point-load and no GitHub call; the bulk
/// fetch happens only on a miss, a TTL expiry, an epoch mismatch, or an explicit
/// resync.
/// </summary>
public interface IUserAccessService
{
    /// <summary>
    /// The current user's snapshot, or null for an anonymous caller (and for a
    /// local account with no GitHub identity, which is entitled to nothing).
    /// </summary>
    Task<UserAccess?> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>Drops the current user's snapshot so the next read rebuilds it.</summary>
    Task InvalidateAsync(CancellationToken cancellationToken = default);
}
