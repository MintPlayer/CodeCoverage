namespace Coverage.Entities;

/// <summary>
/// One user's persisted GitHub entitlement snapshot — the answer to "which
/// repositories may this person see, and which may they administer".
///
/// GitHub remains the authority. This is a **derived cache**, not an ACL:
/// nothing writes it but the sync that rebuilds it from GitHub, nothing reads it
/// as a source of truth, and deleting it costs exactly one refresh. If a human
/// ever gains a way to edit it directly it has become the parallel permission
/// system the architecture deliberately does not have.
///
/// Deliberately absent from <c>App_Data/Model</c>: it has no business on the
/// generic /spark surface, where security.json grants Query/Read to Everyone.
///
/// Staleness is bounded two ways, and both matter. <see cref="BuiltAtUtc"/> is
/// the backstop that still works when a webhook is dropped — a Failed message is
/// never retried here, and a 200 to GitHub does not mean processed — while
/// <see cref="BuiltAgainstEpochs"/> catches installation- and team-level changes
/// within seconds, without needing to know which users a team contains.
/// </summary>
public class UserAccess
{
    public string? Id { get; set; }

    /// <summary>The local Identity user this snapshot belongs to.</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>
    /// Accounts whose repositories this user may see, by GitHub numeric id.
    /// Kept for *list* queries, which cannot afford a per-row decision: a list
    /// may over-include, but serving a specific document may not. See
    /// <see cref="Repositories"/> for the per-repository authority.
    /// </summary>
    public long[] OwnerGitHubIds { get; set; } = [];

    /// <summary>
    /// Per-repository entitlement, from the <c>permissions</c> hash GitHub
    /// returns for each repository of each installation the user can reach.
    /// A repository absent from this list is not entitled — absence is denial.
    /// </summary>
    public List<RepositoryEntitlement> Repositories { get; set; } = [];

    public DateTime BuiltAtUtc { get; set; }

    /// <summary>
    /// The <c>Account.AccessEpoch</c> each owner was at when this snapshot was
    /// built, keyed by account document id. A bumped epoch means an
    /// installation or team change landed since, so the snapshot is stale even
    /// if <see cref="BuiltAtUtc"/> is recent.
    /// </summary>
    public Dictionary<string, int> BuiltAgainstEpochs { get; set; } = [];

    /// <summary>Level for one repository, or <see cref="RepositoryAccessLevel.None"/> when absent.</summary>
    public RepositoryAccessLevel LevelFor(long repositoryGitHubId)
    {
        foreach (var entitlement in Repositories)
        {
            if (entitlement.RepositoryGitHubId == repositoryGitHubId)
                return entitlement.Level;
        }
        return RepositoryAccessLevel.None;
    }

    public static string DocumentId(string userId) => $"UserAccess/{userId}";
}

public class RepositoryEntitlement
{
    public long RepositoryGitHubId { get; set; }
    public RepositoryAccessLevel Level { get; set; }
}

/// <summary>
/// What a viewer may do with one repository. Ordered, so <c>&gt;=</c> reads
/// naturally: <c>Admin</c> implies <c>Read</c>.
/// </summary>
public enum RepositoryAccessLevel
{
    /// <summary>Not visible. Every surface answers 404, never 403 — no existence oracle.</summary>
    None = 0,

    /// <summary>May browse coverage, the file tree, source, and the badge.</summary>
    Read = 1,

    /// <summary>
    /// May additionally mint and revoke upload tokens, rotate the badge token,
    /// and rewrite the gate policy. Maps to GitHub's <c>permissions.admin</c>.
    /// </summary>
    Admin = 2,
}
