using Coverage.Entities;
using Coverage.Services;
using Microsoft.AspNetCore.Mvc;
using MintPlayer.SourceGenerators.Attributes;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;

namespace Coverage.Controllers;

/// <summary>
/// Read API for the browse UI: account → repositories → commits → build →
/// folder tree. Public repositories are world-readable; private ones require
/// the viewer's GitHub-verified access to the owner (no app-local ACL).
/// Anonymous requests simply see the public subset.
/// </summary>
[ApiController]
[Route("api/browse")]
public partial class BrowseController : ControllerBase
{
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly IGitHubAccessService gitHubAccess;

    public sealed record RepoInfo(string Owner, string Name, string FullName, bool IsPrivate, string? DefaultBranch,
        CoverageSummary? LatestCoverage, string? LatestCoverageSha);
    public sealed record CommitInfo(string Sha, string? Branch, int? PullRequestNumber, string? Message,
        DateTimeOffset? AuthoredAt, CoverageSummary? Coverage);
    public sealed record BuildInfo(long RunId, int RunAttempt, string Status, string? FinalizeReason,
        string? WorkflowName, DateTime CreatedAtUtc, CoverageSummary? Coverage, IEnumerable<SessionInfo> Sessions);
    public sealed record SessionInfo(string SessionId, string? JobName, string[] Flags, string ParseStatus, string? Error, int FilesCount);
    public sealed record TreeEntry(string Name, string Path, bool IsFile, int LinesCovered, int LinesCoverable);
    public sealed record TreeResponse(string BuildId, IEnumerable<TreeEntry> Entries, IEnumerable<string> UnmatchedFiles);

    [HttpGet("accounts/{login}/repos")]
    public async Task<ActionResult<IEnumerable<RepoInfo>>> GetAccountRepos(string login, CancellationToken cancellationToken)
    {
        var includePrivate = await gitHubAccess.IsOwnerAllowedAsync(login, cancellationToken);

        var repos = await session.Query<Repository>()
            .Where(r => r.OwnerLogin == login)
            .Take(1024)
            .ToListAsync(cancellationToken);

        return Ok(repos
            .Where(r => includePrivate || !r.IsPrivate)
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .Select(ToRepoInfo));
    }

    [HttpGet("repos/{owner}/{name}")]
    public async Task<ActionResult<RepoInfo>> GetRepo(string owner, string name, CancellationToken cancellationToken)
    {
        var repository = await ResolveVisibleRepository(owner, name, cancellationToken);
        if (repository is null) return NotFound();
        return Ok(ToRepoInfo(repository));
    }

    [HttpGet("repos/{owner}/{name}/commits")]
    public async Task<ActionResult<IEnumerable<CommitInfo>>> GetCommits(
        string owner, string name, [FromQuery] string? branch, [FromQuery] bool withCoverageOnly = true,
        [FromQuery] int skip = 0, [FromQuery] int take = 50, CancellationToken cancellationToken = default)
    {
        var repository = await ResolveVisibleRepository(owner, name, cancellationToken);
        if (repository is null) return NotFound();

        var query = session.Query<Indexes.Commits_ByRepository.Result, Indexes.Commits_ByRepository>()
            .Where(c => c.Repository == repository.Id);
        if (!string.IsNullOrEmpty(branch))
            query = query.Where(c => c.Branch == branch);
        if (withCoverageOnly)
            query = query.Where(c => c.HasCoverage);

        var commits = await query
            .OrderByDescending(c => c.AuthoredAt)
            .Skip(skip)
            .Take(Math.Min(take, 200))
            .OfType<Commit>()
            .ToListAsync(cancellationToken);

        return Ok(commits.Select(c => new CommitInfo(c.Sha, c.Branch, c.PullRequestNumber, c.Message, c.AuthoredAt, c.Coverage)));
    }

    [HttpGet("repos/{owner}/{name}/commits/{sha}")]
    public async Task<ActionResult<object>> GetCommit(string owner, string name, string sha, CancellationToken cancellationToken)
    {
        var repository = await ResolveVisibleRepository(owner, name, cancellationToken);
        if (repository is null) return NotFound();

        var commit = await session.LoadAsync<Commit>(Commit.DocumentId(repository.GitHubId, sha), cancellationToken);
        if (commit is null) return NotFound();

        var builds = new List<Build>();
        await using (var stream = await session.Advanced.StreamAsync<Build>(
            startsWith: $"{Commit.DocumentId(repository.GitHubId, sha)}/builds/", token: cancellationToken))
        {
            while (await stream.MoveNextAsync())
                builds.Add(stream.Current.Document);
        }

        return Ok(new
        {
            commit.Sha,
            commit.Branch,
            commit.PullRequestNumber,
            commit.Message,
            commit.AuthoredAt,
            commit.Coverage,
            commit.LatestBuildId,
            Builds = builds
                .OrderByDescending(b => b.CreatedAtUtc)
                .Select(b => new BuildInfo(b.CiRunId, b.CiRunAttempt, b.Status, b.FinalizeReason, b.WorkflowName,
                    b.CreatedAtUtc, b.Coverage,
                    b.Sessions.Select(s => new SessionInfo(s.SessionId, s.JobName, s.Flags, s.ParseStatus, s.Error, s.FilesCount)))),
        });
    }

    /// <summary>One folder level of the commit's coverage tree (drill-down UI).</summary>
    [HttpGet("repos/{owner}/{name}/commits/{sha}/tree")]
    public async Task<ActionResult<TreeResponse>> GetTree(
        string owner, string name, string sha, [FromQuery] string? path, CancellationToken cancellationToken)
    {
        var repository = await ResolveVisibleRepository(owner, name, cancellationToken);
        if (repository is null) return NotFound();

        var commit = await session.LoadAsync<Commit>(Commit.DocumentId(repository.GitHubId, sha), cancellationToken);
        if (commit?.LatestBuildId is null) return NotFound();

        var files = await LoadBuildFiles(commit.LatestBuildId, cancellationToken);

        var prefix = string.IsNullOrEmpty(path) ? "" : path.TrimEnd('/') + "/";
        var folders = new Dictionary<string, (int Covered, int Coverable)>(StringComparer.Ordinal);
        var entries = new List<TreeEntry>();

        foreach (var file in files.Where(f => f.Matched && f.Path.StartsWith(prefix, StringComparison.Ordinal)))
        {
            var rest = file.Path[prefix.Length..];
            var slash = rest.IndexOf('/');
            var covered = file.Lines.Count(l => l.Status != LineStatus.NotCovered);
            if (slash < 0)
            {
                entries.Add(new TreeEntry(rest, file.Path, true, covered, file.Lines.Count));
            }
            else
            {
                var folder = rest[..slash];
                var current = folders.GetValueOrDefault(folder);
                folders[folder] = (current.Covered + covered, current.Coverable + file.Lines.Count);
            }
        }

        var result = folders
            .Select(kv => new TreeEntry(kv.Key, prefix + kv.Key, false, kv.Value.Covered, kv.Value.Coverable))
            .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .Concat(entries.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase));

        var unmatched = string.IsNullOrEmpty(path)
            ? files.Where(f => !f.Matched).Select(f => f.Path).Take(50)
            : Enumerable.Empty<string>();

        return Ok(new TreeResponse(commit.LatestBuildId, result, unmatched));
    }

    private async Task<List<FileCoverage>> LoadBuildFiles(string buildId, CancellationToken cancellationToken)
    {
        var files = new List<FileCoverage>();
        await using var stream = await session.Advanced.StreamAsync<FileCoverage>(
            startsWith: $"{buildId}/files/", token: cancellationToken);
        while (await stream.MoveNextAsync())
            files.Add(stream.Current.Document);
        return files;
    }

    private async Task<Repository?> ResolveVisibleRepository(string owner, string name, CancellationToken cancellationToken)
    {
        var repository = await session.Query<Repository>()
            .Where(r => r.FullName == $"{owner}/{name}")
            .FirstOrDefaultAsync(cancellationToken);
        if (repository is null) return null;
        if (!repository.IsPrivate) return repository;
        return await gitHubAccess.IsOwnerAllowedAsync(repository.OwnerLogin, cancellationToken) ? repository : null;
    }

    private static RepoInfo ToRepoInfo(Repository r)
        => new(r.OwnerLogin, r.Name, r.FullName, r.IsPrivate, r.DefaultBranch, r.LatestCoverage, r.LatestCoverageSha);
}
