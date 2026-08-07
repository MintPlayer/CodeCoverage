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

    /// <summary>Merged coverage of the latest finalized build, denormalized for lists/badges.</summary>
    public CoverageSummary? Coverage { get; set; }

    public static string DocumentId(long repoGitHubId, string sha) => $"Commits/{repoGitHubId}/{sha}";
}
