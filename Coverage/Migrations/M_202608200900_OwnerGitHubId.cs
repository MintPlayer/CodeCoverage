using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Migrations;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations;
using Raven.Client.Documents.Queries;
using Raven.Client.Documents.Linq;
using Coverage.Entities;

namespace Coverage.Migrations;

/// <summary>
/// Re-keys authorization off the mutable GitHub login and onto the immutable
/// numeric id: backfills <c>Repository.OwnerGitHubId</c> and converts
/// <c>ApiToken.AccountLogin</c> into <c>ApiToken.AccountGitHubId</c>.
///
/// Both are derived from data already in the document — the repository's
/// <c>Account</c> reference is <c>Accounts/{gitHubId}</c>, and a token's account
/// is resolvable through the same collection — so the patches are reproducible
/// and idempotent on replay.
///
/// A repository whose <c>Account</c> is null cannot be keyed (only OIDC
/// auto-provisioning could produce that, and it now refuses rather than storing
/// an unkeyed row). Such rows keep <c>OwnerGitHubId = 0</c>, which no viewer's
/// owner list contains, so they become private-invisible rather than
/// world-visible — the safe direction, and they are re-keyed by the next
/// webhook that touches them.
/// </summary>
public partial class M_202608200900_OwnerGitHubId : ISparkMigration
{
    public static long Version => 202608200900;
    public static string? Description => "Key repositories and upload tokens on the owner's GitHub id, not their login";

    [Inject] private readonly IDocumentStore store;

    /// <summary>Both collections are small; a cap keeps the migration bounded rather than open-ended.</summary>
    private const int CountCap = 4096;

    /// <summary>Just enough of the pre-migration shape to read the old field.</summary>
    private sealed class ApiTokenRow
    {
        public string? Id { get; set; }
        public string? AccountLogin { get; set; }
    }

    public async Task UpAsync(CancellationToken cancellationToken)
    {
        // Accounts/{gitHubId} -> the trailing segment is the id. Guarded on the
        // prefix so a malformed reference is skipped rather than parsed to NaN.
        var repositories = await store.Operations.SendAsync(
            new PatchByQueryOperation(new IndexQuery
            {
                Query = """
                    from Repositories as r
                    update {
                        if (r.Account && r.Account.indexOf('Accounts/') === 0) {
                            r.OwnerGitHubId = parseInt(r.Account.substring('Accounts/'.length), 10);
                        } else {
                            r.OwnerGitHubId = 0;
                        }
                    }
                    """,
            }),
            token: cancellationToken);
        await repositories.WaitForCompletionAsync(TimeSpan.FromMinutes(5));

        // In C#, not a JS patch: a RavenDB patch script can load() by id but cannot
        // query, and a login is not an id. The first version of this called a
        // query() helper that does not exist in the patch API, which threw
        // "Cannot convert undefined or null to object" and aborted startup.
        //
        // Token and account counts are both small, so one session covers it.
        using var session = store.OpenAsyncSession();
        var accounts = await session.Query<Account>()
            .Take(CountCap)
            .ToListAsync(cancellationToken);
        var idByLogin = accounts
            .Where(a => !string.IsNullOrEmpty(a.Login))
            .GroupBy(a => a.Login, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().GitHubId, StringComparer.OrdinalIgnoreCase);

        var tokens = await session.Advanced
            .AsyncRawQuery<ApiTokenRow>("from ApiTokens where AccountLogin != null")
            .Take(CountCap)
            .ToListAsync(cancellationToken);

        foreach (var row in tokens)
        {
            if (row.Id is null || row.AccountLogin is null) continue;
            if (!idByLogin.TryGetValue(row.AccountLogin, out var gitHubId)) continue;

            // Patch the two fields directly so nothing else on the document is
            // rewritten, and so a token whose owner we cannot resolve is left
            // exactly as it was rather than silently losing its scope.
            session.Advanced.Patch<ApiToken, long?>(row.Id, t => t.AccountGitHubId, gitHubId);
        }

        if (session.Advanced.HasChanges)
            await session.SaveChangesAsync(cancellationToken);
    }
}
