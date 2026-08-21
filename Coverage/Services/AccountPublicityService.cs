using Coverage.Entities;
using MintPlayer.SourceGenerators.Attributes;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;

namespace Coverage.Services;

[Register(typeof(IAccountPublicityService), ServiceLifetime.Scoped)]
public partial class AccountPublicityService : IAccountPublicityService
{
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly ILogger<AccountPublicityService> logger;

    /// <summary>Only ">0" is ever asked of this, so a cap costs nothing.</summary>
    private const int CountCap = 4096;

    public void Adjust(Account account, int delta)
    {
        if (delta == 0) return;

        var before = account.PublicRepoCount;
        account.PublicRepoCount = Math.Max(0, before + delta);

        if (account.PublicRepoCount == 0 && before > 0)
        {
            logger.LogInformation(
                "Account {Login} ({GitHubId}) has no public repositories left — it is no longer anonymously visible",
                account.Login, account.GitHubId);
        }
        else
        {
            logger.LogDebug("Account {Login} public repositories {Before} -> {After}",
                account.Login, before, account.PublicRepoCount);
        }
    }

    public async Task ReconcileAllAsync(CancellationToken cancellationToken = default)
    {
        if (session.Advanced.HasChanges)
        {
            // Reading the index around pending writes would persist a stale count,
            // which is the exact failure the delta design exists to avoid.
            logger.LogWarning("Publicity reconciliation skipped: the session has pending changes");
            return;
        }

        var accounts = await session.Query<Account, Indexes.Accounts_Overview>()
            .Take(CountCap)
            .ToListAsync(cancellationToken);

        var drifted = 0;
        foreach (var account in accounts)
        {
            var actual = await session.Query<Repository, Indexes.Repositories_Overview>()
                .Where(r => r.OwnerGitHubId == account.GitHubId && !r.IsPrivate)
                .Take(CountCap)
                .CountAsync(cancellationToken);

            if (account.PublicRepoCount == actual) continue;

            drifted++;
            logger.LogWarning(
                "Reconciled account {Login} ({GitHubId}): public repositories recorded as {Before}, actually {After}",
                account.Login, account.GitHubId, account.PublicRepoCount, actual);
            account.PublicRepoCount = actual;
        }

        if (drifted > 0)
        {
            await session.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Publicity reconciliation corrected {Count} of {Total} accounts", drifted, accounts.Count);
        }
    }
}
