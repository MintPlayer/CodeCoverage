namespace Coverage.Entities;

/// <summary>
/// Per-file line totals of one Build, materialized at finalize so the tree
/// and hierarchy endpoints read one small document instead of re-streaming
/// every FileCoverage (full line arrays) on each request. Document id is
/// {buildId}/tree; re-finalizing overwrites it. Not exposed through Spark's
/// generic UI.
/// </summary>
public class BuildTreeSummary
{
    public string? Id { get; set; }

    public string BuildId { get; set; } = string.Empty;

    public List<TreeFileSummary> Files { get; set; } = [];

    public static string DocumentId(string buildId) => $"{buildId}/tree";
}

public class TreeFileSummary
{
    /// <summary>Normalized repo-relative path with forward slashes.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>False when the path couldn't be matched to the repo file list.</summary>
    public bool Matched { get; set; } = true;

    public int LinesCovered { get; set; }

    public int LinesCoverable { get; set; }
}
