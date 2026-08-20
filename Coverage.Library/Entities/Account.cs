using MintPlayer.Spark.Abstractions;

namespace Coverage.Entities;

/// <summary>
/// A GitHub user or organization that owns repositories. Created/updated from
/// GitHub App installation webhooks; document id is Accounts/{GitHubId} so
/// webhook upserts are idempotent.
/// </summary>
[GenerateIndex]
public class Account
{
    public string? Id { get; set; }

    public long GitHubId { get; set; }

    public string Login { get; set; } = string.Empty;

    /// <summary>"User" or "Organization".</summary>
    public string Type { get; set; } = "User";

    public string? AvatarUrl { get; set; }

    /// <summary>GitHub App installation on this account, when the app is installed.</summary>
    public long? InstallationId { get; set; }

    /// <summary>
    /// Bumped whenever something happens that could change *anyone's* access to
    /// this account's repositories — an installation change, or a team gaining
    /// or losing repository access.
    ///
    /// This is how a <see cref="UserAccess"/> snapshot is invalidated without a
    /// fan-out write and without asking GitHub who is in a team: the snapshot
    /// records the epoch it was built against, and a mismatch means rebuild on
    /// next use. Team webhooks name a team, never its members, so resolving them
    /// to users would cost a members lookup per event — one integer here costs
    /// nothing and covers every member at once.
    /// </summary>
    public int AccessEpoch { get; set; }

    public static string DocumentId(long gitHubId) => $"Accounts/{gitHubId}";
}
