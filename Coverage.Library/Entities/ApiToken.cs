namespace Coverage.Entities;

/// <summary>
/// An upload credential for CI. The document id is ApiTokens/{sha256-hex of
/// the token value}, so global uniqueness holds by construction and lookup is
/// a point-load; the plaintext value is shown once at creation and never stored.
///
/// App-local for now — designed for extraction into a generic
/// MintPlayer.Spark.Authorization.ApiTokens library (docs/spark-handoff.md §2).
/// </summary>
public class ApiToken
{
    public string? Id { get; set; }

    /// <summary>"Account" (all repos of a user/org) or "Repository" (one repo).</summary>
    public string Scope { get; set; } = "Account";

    /// <summary>Owner login this token uploads for, when Scope is "Account".</summary>
    public string? AccountLogin { get; set; }

    /// <summary>GitHub repository id this token uploads for, when Scope is "Repository".</summary>
    public long? RepositoryGitHubId { get; set; }

    public string? Description { get; set; }

    public string CreatedByUserId { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; }

    public DateTime? RevokedAtUtc { get; set; }

    public static string DocumentId(string tokenHash) => $"ApiTokens/{tokenHash}";
}
