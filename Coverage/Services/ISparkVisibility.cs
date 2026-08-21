namespace Coverage.Services;

/// <summary>
/// Per-request visibility snapshots for the Spark row-security hooks. The hooks run
/// up to three times per detail read (once per action) plus once per save, and the
/// redaction hook runs per row — so every answer here is memoized for the request.
/// The underlying owner list is additionally cached ~5 minutes per user by
/// <see cref="IGitHubAccessService"/>.
/// </summary>
public interface ISparkVisibility
{
    /// <summary>
    /// Numeric GitHub ids of the **repositories** the current viewer is entitled
    /// to, from their persisted snapshot; empty for anonymous viewers. Feeds the
    /// Repository row filter, which adds the public subset on top.
    /// </summary>
    Task<long[]> GetEntitledRepositoryGitHubIdsAsync();

    /// <summary>
    /// Numeric GitHub ids of accounts the viewer can reach. Only for deciding
    /// what to *list* (and for the manage gate) — never for deciding whether one
    /// specific repository may be served.
    /// </summary>
    Task<long[]> GetAllowedOwnerIdsAsync();

    /// <summary>
    /// Document ids of repositories the current viewer may see (public ones plus
    /// those of GitHub-granted owners). Feeds the Commit row filter as an IN list
    /// and the Build per-row check.
    /// </summary>
    Task<string[]> GetVisibleRepositoryIdsAsync();

    /// <summary>
    /// Whether the viewer administers this repository — the gate on BadgeToken,
    /// which is a credential however narrow. Snapshot-only: this runs once per
    /// row in the redaction hook, so it must never trigger a GitHub call.
    /// </summary>
    Task<bool> CanManageRepositoryAsync(long repositoryGitHubId);

    /// <summary>
    /// Whether the viewer administers at least one repository of this account —
    /// the gate on account-level operational detail (InstallationId).
    /// </summary>
    Task<bool> CanManageAccountAsync(long accountGitHubId);
}
