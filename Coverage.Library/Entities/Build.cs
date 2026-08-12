using MintPlayer.Spark.Abstractions;

namespace Coverage.Entities;

/// <summary>
/// One CI run's coverage bundle for a commit. All uploads from the same workflow
/// run (runId + runAttempt) land on the same Build as sessions and are merged.
/// Document id is Commits/{repoGitHubId}/{sha}/builds/{runId}-{runAttempt}.
/// </summary>
[Breadcrumb("{CiRunId}")]
public class Build
{
    public string? Id { get; set; }

    [Reference(typeof(Commit))]
    public string? Commit { get; set; }

    /// <summary>"Open" while uploads may still arrive; "Finalized" once closed.</summary>
    public string Status { get; set; } = "Open";

    public long CiRunId { get; set; }

    public int CiRunAttempt { get; set; }

    public string? WorkflowName { get; set; }

    public string? EventName { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime? LastUploadAtUtc { get; set; }

    public DateTime? FinalizedAtUtc { get; set; }

    /// <summary>"Explicit" | "Debounce" | "Timeout"</summary>
    public string? FinalizeReason { get; set; }

    public List<BuildSession> Sessions { get; set; } = [];

    public CoverageSummary? Coverage { get; set; }

    public static string DocumentId(long repoGitHubId, string sha, long runId, int runAttempt)
        => $"{Entities.Commit.DocumentId(repoGitHubId, sha)}/builds/{runId}-{runAttempt}";
}
