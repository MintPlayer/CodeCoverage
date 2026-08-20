using MintPlayer.Spark.Abstractions;

namespace Coverage.Entities;

/// <summary>
/// A GitHub repository the app knows about (installed via the GitHub App).
/// Document id is Repositories/{GitHubId} so webhook upserts are idempotent.
/// </summary>
[GenerateIndex]
public class Repository
{
    public string? Id { get; set; }

    [Reference(typeof(Account))]
    public string? Account { get; set; }

    public long GitHubId { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>owner/name</summary>
    public string FullName { get; set; } = string.Empty;

    /// <summary>
    /// Display and URL segment only — never an authorization key. A GitHub
    /// login is mutable (org rename, or a freed login taken over by someone
    /// else), so gating on it both detaches repos from their viewers silently
    /// and lets a stale local account authorize as the new owner. Use
    /// <see cref="OwnerGitHubId"/> for every entitlement decision.
    /// </summary>
    public string OwnerLogin { get; set; } = string.Empty;

    /// <summary>
    /// The owning account's immutable GitHub numeric id — the authorization
    /// key for this repository. Denormalized from <see cref="Account"/> (which
    /// is nullable and a document-id string) so the row filter can express
    /// itself as an indexable <c>In()</c> over longs.
    /// </summary>
    public long OwnerGitHubId { get; set; }

    /// <summary>
    /// Mirror of GitHub's visibility, and the gate in front of every read path.
    /// Treat it as a **lease, not an assertion** — see
    /// <see cref="VisibilityCheckedAtUtc"/>. Written only by the webhook path and
    /// by the visibility refresh; never trust it past its lease.
    /// </summary>
    public bool IsPrivate { get; set; }

    /// <summary>
    /// When <see cref="IsPrivate"/> was last confirmed against GitHub.
    ///
    /// Without this the flag is a permanent claim, and that is exactly how a repo
    /// could stay world-readable after being made private: OIDC-provisioned
    /// repositories have no App installation, so the webhook that corrects the
    /// flag never fires for them, and nothing else ever looked. Null means never
    /// confirmed, which counts as expired.
    /// </summary>
    public DateTime? VisibilityCheckedAtUtc { get; set; }

    public string? DefaultBranch { get; set; }

    public bool Archived { get; set; }

    /// <summary>
    /// Grants access to the rendered badge SVG only — never report data.
    /// Set for private repositories; independently rotatable.
    ///
    /// [IgnoreForIndex] because index membership is opt-out: without it this
    /// lands in VRepository, and synchronize then marks every projected field
    /// queryable — putting a live badge token in the /spark repository grid,
    /// which security.json grants to Everyone. Nothing filters or sorts on it,
    /// so the index has no use for it either.
    /// </summary>
    [IgnoreForIndex]
    public string? BadgeToken { get; set; }

    /// <summary>
    /// Gate policy; null means every default (informational, auto-ratchet).
    /// [IgnoreForIndex]: policy is owner-facing configuration — it has no
    /// business in the anonymous /spark grid, and nothing filters on it.
    /// </summary>
    [IgnoreForIndex]
    public GateSettings? Gate { get; set; }

    /// <summary>
    /// Denormalized from the newest finalized default-branch build, so repo
    /// lists and badges are point-loads.
    /// </summary>
    public CoverageSummary? LatestCoverage { get; set; }

    public string? LatestCoverageSha { get; set; }

    public DateTime? LatestCoverageAtUtc { get; set; }

    public static string DocumentId(long gitHubId) => $"Repositories/{gitHubId}";
}
