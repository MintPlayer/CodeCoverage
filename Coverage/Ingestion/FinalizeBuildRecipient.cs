using Coverage.Entities;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Messaging.Abstractions;
using Raven.Client.Documents.Session;

namespace Coverage.Ingestion;

public partial class FinalizeBuildRecipient : IRecipient<FinalizeBuildMessage>
{
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly ILogger<FinalizeBuildRecipient> logger;

    public async Task HandleAsync(FinalizeBuildMessage message, CancellationToken cancellationToken = default)
    {
        var build = await session.LoadAsync<Build>(message.BuildId, cancellationToken);
        if (build is null)
        {
            logger.LogWarning("Build {BuildId} not found — skipping finalize", message.BuildId);
            return;
        }

        await BuildFinalizer.Finalize(session, build, "Explicit", cancellationToken);
        await session.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Finalized build {BuildId} (Explicit)", message.BuildId);
    }
}
