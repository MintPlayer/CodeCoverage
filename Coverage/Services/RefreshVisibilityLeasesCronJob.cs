using Coverage.Entities;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Cron;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;

namespace Coverage.Services;

/// <summary>
/// Renews expired public/private leases in the background.
///
/// The read paths renew the lease of whatever they are asked for, which covers
/// <c>/api/browse</c> and the badge. It does not cover the surfaces that cannot
/// renew inline: Spark's generic <c>/spark</c> grid decides visibility with one
/// RavenDB row filter over the whole collection, and a filter cannot make a
/// GitHub call per row. Without this job a repository made private on GitHub
/// would keep appearing in that grid until somebody happened to open its page.
///
/// Cluster-safe via Spark cron's compare-exchange claim. Deliberately small and
/// slow: a bounded batch every few minutes spends the rate limit on the oldest
/// leases rather than crawling everything at once, and the inline renewals carry
/// anything a viewer actually looks at.
/// </summary>
public partial class RefreshVisibilityLeasesCronJob : ISparkCronJob
{
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly IGitHubEntitlementSource entitlementSource;
    [Inject] private readonly ILogger<RefreshVisibilityLeasesCronJob> logger;

    public static string CronSchedule => "*/5 * * * *";

    /// <summary>Matches <c>RepositoryAccessService.VisibilityLease</c>.</summary>
    private static readonly TimeSpan Lease = TimeSpan.FromHours(6);

    /// <summary>
    /// Small on purpose. A repository with no installation falls back to an
    /// unauthenticated read, metered at 60/hr per IP, so a large batch would
    /// exhaust that budget and start returning 403s — which this job correctly
    /// treats as "no evidence" and would therefore burn for nothing.
    /// </summary>
    private const int BatchSize = 20;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var cutoff = DateTime.UtcNow - Lease;

        var stale = await session.Query<Repository, Indexes.Repositories_Overview>()
            .Where(r => r.VisibilityCheckedAtUtc == null || r.VisibilityCheckedAtUtc < cutoff)
            .OrderBy(r => r.VisibilityCheckedAtUtc)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);

        if (stale.Count == 0) return;

        logger.LogInformation("Visibility lease sweep: {Count} repositories with an expired lease", stale.Count);

        var accountIds = stale
            .Where(r => r.Account is not null)
            .Select(r => r.Account!)
            .Distinct()
            .ToArray();
        var accounts = await session.LoadAsync<Account>(accountIds, cancellationToken);

        var flipped = 0;
        foreach (var repository in stale)
        {
            var installationId = repository.Account is not null
                ? accounts.GetValueOrDefault(repository.Account)?.InstallationId
                : null;

            var publiclyVisible = await entitlementSource.IsPubliclyVisibleAsync(
                repository.FullName, installationId, cancellationToken);
            if (publiclyVisible is null)
            {
                // No evidence. Leave both the flag and the stamp alone so the next
                // sweep retries this repository rather than trusting silence.
                continue;
            }

            if (publiclyVisible.Value is false && !repository.IsPrivate)
            {
                flipped++;
                logger.LogWarning(
                    "Visibility lease sweep: {FullName} is no longer public — flipping to private. "
                    + "Its badge will render 'unknown' until a badge token is minted.",
                    repository.FullName);
            }

            repository.IsPrivate = !publiclyVisible.Value;
            repository.VisibilityCheckedAtUtc = DateTime.UtcNow;
        }

        if (session.Advanced.HasChanges)
            await session.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Visibility lease sweep: renewed {Count}, flipped {Flipped} to private",
            stale.Count, flipped);
    }
}
