using MintPlayer.Spark.Abstractions;

namespace Coverage.Entities;

/// <summary>
/// A commit we have seen (via push/pull_request webhooks or a coverage upload).
/// Document id is Commits/{repoGitHubId}/{sha} so any source can upsert idempotently.
/// </summary>
[Breadcrumb("{Sha}")]
public class Commit
{
    public string? Id { get; set; }

    [Reference(typeof(Repository))]
    public string? Repository { get; set; }

    public string Sha { get; set; } = string.Empty;

    public string? Branch { get; set; }

    public int? PullRequestNumber { get; set; }

    public string? ParentSha { get; set; }

    public string? Message { get; set; }

    public DateTimeOffset? AuthoredAt { get; set; }

    /// <summary>
    /// When this document was first created, by whichever path saw the commit
    /// first (webhook or upload). AuthoredAt only arrives via push/PR webhooks,
    /// so upload-only commits (the norm for OIDC auto-provisioned repos) would
    /// otherwise have nothing to sort by — lists order by AuthoredAt coalesced
    /// with this.
    /// </summary>
    public DateTimeOffset? FirstSeenAtUtc { get; set; }

    /// <summary>Merged coverage of the latest finalized build, denormalized for lists/badges.</summary>
    public CoverageSummary? Coverage { get; set; }

    /// <summary>The build whose coverage is shown for this commit (file tree reads its FileCoverage docs).</summary>
    public string? LatestBuildId { get; set; }

    public static string DocumentId(long repoGitHubId, string sha) => $"Commits/{repoGitHubId}/{sha}";
}
