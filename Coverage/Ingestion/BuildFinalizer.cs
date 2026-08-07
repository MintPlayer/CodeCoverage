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
                commit.LatestBuildId = build.Id;

                if (commit.Repository is not null)
                {
                    var repository = await session.LoadAsync<Repository>(commit.Repository, cancellationToken);
                    // Repo-level coverage tracks the default branch; a repo that
                    // never had data accepts any branch rather than showing nothing.
                    if (repository is not null
                        && (repository.LatestCoverage is null
                            || repository.DefaultBranch is null
                            || string.Equals(commit.Branch, repository.DefaultBranch, StringComparison.Ordinal)))
                    {
                        repository.LatestCoverage = build.Coverage;
                        repository.LatestCoverageSha = commit.Sha;
                        repository.LatestCoverageAtUtc = DateTime.UtcNow;
                    }
                }
            }
        }
    }
}
