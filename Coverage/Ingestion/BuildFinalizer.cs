using Coverage.Entities;
using Raven.Client.Documents.Session;

namespace Coverage.Ingestion;

/// <summary>
/// Closing a Build promotes its merged coverage onto the Commit (which lists,
/// badges and deltas read) and materializes the per-file tree summary the
/// browse endpoints serve. Caller owns SaveChanges.
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

        if (build.Id is not null)
            await MaterializeTreeSummary(session, build.Id, cancellationToken);

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

                    await StampCoverageDelta(session, commit, repository, cancellationToken);
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

    /// <summary>
    /// Records the commit's coverage change versus its parent. Both sides must
    /// be known — an unseen parent, or one whose own build hasn't finalized
    /// yet, leaves the delta null rather than implying a change from zero.
    /// (A parent that finalizes later does not retro-stamp its children; those
    /// keep showing no delta until their own next build.)
    /// </summary>
    private static async Task StampCoverageDelta(
        IAsyncDocumentSession session, Commit commit, Repository? repository, CancellationToken cancellationToken)
    {
        commit.CoverageDelta = null;
        if (repository is null || string.IsNullOrEmpty(commit.ParentSha))
            return;

        var parent = await session.LoadAsync<Commit>(
            Commit.DocumentId(repository.GitHubId, commit.ParentSha), cancellationToken);

        var current = Percent(commit.Coverage);
        var previous = Percent(parent?.Coverage);
        if (current is not null && previous is not null)
            commit.CoverageDelta = current - previous;
    }

    private static double? Percent(CoverageSummary? summary)
        => summary is { LinesCoverable: > 0 }
            ? summary.LinesCovered * 100d / summary.LinesCoverable
            : null;

    private static async Task MaterializeTreeSummary(IAsyncDocumentSession session, string buildId, CancellationToken cancellationToken)
    {
        var summary = new BuildTreeSummary { BuildId = buildId };
        await using (var stream = await session.Advanced.StreamAsync<FileCoverage>(
            startsWith: $"{buildId}/files/", token: cancellationToken))
        {
            while (await stream.MoveNextAsync())
            {
                var file = stream.Current.Document;
                summary.Files.Add(new TreeFileSummary
                {
                    Path = file.Path,
                    Matched = file.Matched,
                    LinesCovered = file.Lines.Count(l => l.Status != LineStatus.NotCovered),
                    LinesCoverable = file.Lines.Count,
                });
            }
        }

        await session.StoreAsync(summary, BuildTreeSummary.DocumentId(buildId), cancellationToken);
    }
}
