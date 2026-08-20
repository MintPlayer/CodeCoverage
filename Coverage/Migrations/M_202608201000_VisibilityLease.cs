using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Migrations;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations;
using Raven.Client.Documents.Queries;

namespace Coverage.Migrations;

/// <summary>
/// Expires the public/private lease on every existing repository, so each is
/// re-confirmed against GitHub the first time it is read.
///
/// <c>IsPrivate</c> was written once and trusted forever. For repositories
/// auto-provisioned from an OIDC upload it was hardcoded <c>false</c> with no App
/// installation behind it, so the webhook that corrects it could never fire —
/// meaning any of those later made private on GitHub has been serving coverage,
/// file paths and its badge to the public internet ever since. Clearing the
/// stamp is what forces that recheck; it is deliberately not a bulk GitHub
/// crawl, which would spend the rate limit on repositories nobody is looking at.
///
/// Idempotent: it writes null, so a replay is a no-op beyond the write.
/// </summary>
public partial class M_202608201000_VisibilityLease : ISparkMigration
{
    public static long Version => 202608201000;
    public static string? Description => "Expire every repository's visibility lease so it is re-confirmed on first read";

    [Inject] private readonly IDocumentStore store;

    public async Task UpAsync(CancellationToken cancellationToken)
    {
        var operation = await store.Operations.SendAsync(
            new PatchByQueryOperation(new IndexQuery
            {
                Query = "from Repositories update { this.VisibilityCheckedAtUtc = null; }",
            }),
            token: cancellationToken);
        await operation.WaitForCompletionAsync(TimeSpan.FromMinutes(5));

        // Accounts start at epoch 0; the property simply appears on first write.
        var accounts = await store.Operations.SendAsync(
            new PatchByQueryOperation(new IndexQuery
            {
                Query = "from Accounts update { this.AccessEpoch = this.AccessEpoch || 0; }",
            }),
            token: cancellationToken);
        await accounts.WaitForCompletionAsync(TimeSpan.FromMinutes(5));
    }
}
