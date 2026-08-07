using MintPlayer.Spark.Abstractions;

namespace Coverage.Entities;

/// <summary>
/// A GitHub repository the app knows about (installed via the GitHub App).
/// Document id is Repositories/{GitHubId} so webhook upserts are idempotent.
/// </summary>
[Breadcrumb("{FullName}")]
public class Repository
{
    public string? Id { get; set; }

    [Reference(typeof(Account))]
    public string? Account { get; set; }

    public long GitHubId { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>owner/name</summary>
    public string FullName { get; set; } = string.Empty;

    public string OwnerLogin { get; set; } = string.Empty;

    public bool IsPrivate { get; set; }

    public string? DefaultBranch { get; set; }

    public bool Archived { get; set; }

    /// <summary>
    /// Grants access to the rendered badge SVG only — never report data.
    /// Set for private repositories; independently rotatable.
    /// </summary>
    public string? BadgeToken { get; set; }

    /// <summary>
    /// Denormalized from the newest finalized default-branch build, so repo
    /// lists and badges are point-loads.
    /// </summary>
    public CoverageSummary? LatestCoverage { get; set; }

    public string? LatestCoverageSha { get; set; }

    public DateTime? LatestCoverageAtUtc { get; set; }

    public static string DocumentId(long gitHubId) => $"Repositories/{gitHubId}";
}
