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

    /// <summary>
    /// GitHub App installation on this account, when the app is installed.
    ///
    /// [IgnoreForIndex] because redaction nulls the value but cannot remove the
    /// attribute from the model — and a declared attribute is an accepted
    /// <c>sortColumns</c> value, so an anonymous caller could order the grid by a
    /// field they are not allowed to read. Removing it from the index removes the
    /// channel rather than papering over it; nothing filters or sorts on it.
    /// </summary>
    [IgnoreForIndex]
    public long? InstallationId { get; set; }

    /// <summary>
    /// How many of this account's repositories are currently public.
    ///
    /// Denormalized because it is the Account row filter, and a row filter has to
    /// be one RavenDB-translatable expression — it cannot join to Repositories.
    /// Without it every account is anonymously enumerable, including accounts that
    /// exist *only* because they own private repositories, which discloses who
    /// uses this service.
    ///
    /// Maintained exactly by the webhook and provisioning paths, and reconciled
    /// periodically by the visibility sweep so drift heals itself rather than
    /// silently hiding or exposing an account forever.
    /// </summary>
    public int PublicRepoCount { get; set; }

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
