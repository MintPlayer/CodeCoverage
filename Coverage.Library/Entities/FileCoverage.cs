using System.Security.Cryptography;
using System.Text;

namespace Coverage.Entities;

/// <summary>
/// Merged per-file coverage for one Build (max across its sessions).
/// Document id is {buildId}/files/{pathHash} so re-parsing a session
/// overwrites deterministically. Not exposed through Spark's generic UI.
/// </summary>
public class FileCoverage
{
    public string? Id { get; set; }

    public string BuildId { get; set; } = string.Empty;

    /// <summary>Normalized repo-relative path with forward slashes.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>False when the path couldn't be matched to the repo file list.</summary>
    public bool Matched { get; set; } = true;

    public List<LineCoverage> Lines { get; set; } = [];

    public List<BranchCoverage> Branches { get; set; } = [];

    public static string DocumentId(string buildId, string normalizedPath)
        => $"{buildId}/files/{PathHash(normalizedPath)}";

    public static string PathHash(string normalizedPath)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalizedPath));
        return Convert.ToHexStringLower(bytes)[..20];
    }
}

public class LineCoverage
{
    public int Number { get; set; }

    /// <summary>Execution count; null when the source format has none (e.g. JaCoCo).</summary>
    public int? Hits { get; set; }

    public LineStatus Status { get; set; }
}

public class BranchCoverage
{
    public int Line { get; set; }

    /// <summary>Branching location within the line (format-specific block id).</summary>
    public string BlockId { get; set; } = string.Empty;

    /// <summary>One edge of the branching expression.</summary>
    public string BranchId { get; set; } = string.Empty;

    /// <summary>Times this edge was taken; null when the enclosing block never executed.</summary>
    public int? Taken { get; set; }
}
