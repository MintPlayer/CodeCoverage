using Coverage.Entities;
using Raven.Client.Documents.Session;

namespace Coverage.Ingestion;

/// <summary>
/// Closing a Build promotes its merged coverage onto the Commit (which lists,
/// badges and deltas read). Caller owns SaveChanges.
/// </summary>
public static class BuildFinalizer
{
    public static async Task Finalize(IAsyncDocumentSession session, Build build, string reason, CancellationToken cancellationToken)
    {
        if (build.Status == "Finalized")
            return;

        build.Status = "Finalized";
        build.FinalizedAtUtc = DateTime.UtcNow;
        build.FinalizeReason = reason;

        if (build.Commit is not null)
        {
            var commit = await session.LoadAsync<Commit>(build.Commit, cancellationToken);
            if (commit is not null)
            {
                commit.Coverage = build.Coverage;
            }
        }
    }
}
