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

    /// <summary>
    /// Computed "runId.attempt" display value for the generic grids (master-parity
    /// "Run" column). Deterministic from the two stored fields; get-only, so the
    /// Spark mapper serves it as an attribute without any Actions-class code.
    /// </summary>
    public string Run => $"{CiRunId}.{CiRunAttempt}";

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

    /// <summary>
    /// The one classification an API consumer is invited to branch on: is this
    /// build still working, did it finish cleanly, or did it finish with a
    /// number that under-counts? Derived here rather than by each caller so the
    /// status endpoint, the UI and any future check-run publisher can never
    /// disagree about what a build "is".
    /// <para>
    /// The internal vocabulary behind it is deliberately not the contract,
    /// because it is not frozen: T1.2 adds a partial-parse status for a session
    /// where only some reports were readable. Hence the shape of the two tests
    /// below — only "Pending" counts as in-flight, and cleanliness is
    /// "everything is exactly Parsed" rather than "nothing is Failed", so a new
    /// terminal status is absorbed into <c>CompleteWithErrors</c> without any
    /// consumer changing.
    /// </para>
    /// </summary>
    public static string ClassifyState(Build build)
    {
        if (build.Status != "Finalized" || build.Sessions.Any(s => s.ParseStatus == "Pending"))
            return "InFlight";

        // FinalizeReason "Timeout" already implies a Failed session — the cron
        // marks stragglers before closing — so this is belt-and-braces against a
        // future finalize path that times out without doing so.
        var clean = build.Sessions.All(s => s.ParseStatus == "Parsed") && build.FinalizeReason != "Timeout";
        return clean ? "Complete" : "CompleteWithErrors";
    }
}
