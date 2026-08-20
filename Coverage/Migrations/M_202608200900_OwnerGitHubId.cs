using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Migrations;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations;
using Raven.Client.Documents.Queries;

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

        // load() by the document id the login resolves to is not available here
        // (logins are not ids), so resolve through the Accounts collection. Token
        // counts are small, so the correlated subquery cost is irrelevant.
        var tokens = await store.Operations.SendAsync(
            new PatchByQueryOperation(new IndexQuery
            {
                Query = """
                    from ApiTokens as t
                    where t.AccountLogin != null
                    update {
                        var owner = null;
                        for (var a of query("from Accounts")) {
                            if (a.Login && t.AccountLogin
                                && a.Login.toLowerCase() === t.AccountLogin.toLowerCase()) {
                                owner = a;
                                break;
                            }
                        }
                        if (owner) {
                            t.AccountGitHubId = owner.GitHubId;
                            delete t.AccountLogin;
                        }
                    }
                    """,
            }),
            token: cancellationToken);
        await tokens.WaitForCompletionAsync(TimeSpan.FromMinutes(5));
    }
}
