namespace Coverage.Entities;

/// <summary>
/// One row of the home page's "your accounts" grid: an account the signed-in
/// viewer can reach, with the two figures that depend on *who is asking*.
///
/// <para>
/// **Not a RavenDB document.** Rows are computed per request from the viewer's
/// entitlement snapshot and never stored. It exists as its own type only because
/// Spark's grid columns are per *entity*, not per query — so putting
/// <see cref="RepositoryCount"/> and <see cref="AggregateCoverage"/> on
/// <c>Account</c> also puts them on the anonymous accounts grid, where nothing
/// computes them and they render empty. A type of its own gets its own model file
/// and therefore its own columns. See Spark#284; when that ships, this can
/// collapse back into <c>Account</c>.
/// </para>
/// <para>
/// <see cref="Id"/> is the account's document id rather than a synthetic key.
/// Spark finishes a query with <c>DistinctBy(po =&gt; po.Id)</c>, so rows sharing
/// a null id collapse into one — and using the real id means a row for an owner
/// we hold no <c>Account</c> document for is still distinct, which is the case
/// that exists because GitHub reports installations before our backfill runs.
/// </para>
/// </summary>
public class MyAccountRow
{
    /// <summary>The account's document id — see the note on collapsing above.</summary>
    public string? Id { get; set; }

    public long GitHubId { get; set; }

    public string Login { get; set; } = string.Empty;

    /// <summary>"User" or "Organization".</summary>
    public string Type { get; set; } = "User";

    public string? AvatarUrl { get; set; }

    /// <summary>
    /// Whether the GitHub App is installed on this account. A boolean rather than
    /// the installation id, which is redacted from anyone who administers nothing
    /// here — the *fact* of an installation is not sensitive for an account the
    /// viewer can already see, but the id is.
    /// </summary>
    public bool IsAppInstalled { get; set; }

    /// <summary>
    /// How many of this account's repositories *this viewer* may see. Counting all
    /// of them would disclose the existence of private repositories the viewer has
    /// no entitlement to, which is what per-repository entitlement exists to stop.
    /// </summary>
    public int RepositoryCount { get; set; }

    /// <summary>
    /// Line coverage across those same repositories, or null when none of them has
    /// coverage yet.
    /// </summary>
    public double? AggregateCoverage { get; set; }
}
