using Coverage.ApiTokens;
using Coverage.Entities;
using Coverage.Ingestion;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Messaging.Abstractions;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;

namespace Coverage.Controllers;

/// <summary>
/// Coverage-report ingestion. Authenticated with an upload token (ApiToken
/// scheme); the same action may POST several times per workflow run — each call
/// becomes a session on the run's Build, parsed asynchronously via the message
/// bus. 202 means "accepted for processing", never "parsed".
/// </summary>
[ApiController]
[Route("api/uploads")]
[Authorize(AuthenticationSchemes = ApiTokenAuthenticationHandler.SchemeName)]
[EnableRateLimiting("uploads")]
public partial class UploadsController : ControllerBase
{
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly IMessageBus messageBus;
    [Inject] private readonly ILogger<UploadsController> logger;

    private const long MaxReportBytes = 50 * 1024 * 1024;

    public sealed record UploadResponse(string BuildId, string SessionId);

    [HttpPost]
    [RequestSizeLimit(MaxReportBytes)]
    public async Task<ActionResult<UploadResponse>> Upload([FromForm] UploadForm form, CancellationToken cancellationToken)
    {
        if (form.Files.Count == 0)
            return BadRequest(new { error = "No coverage report files in the upload." });
        if (string.IsNullOrWhiteSpace(form.Repository) || !form.Repository.Contains('/'))
            return BadRequest(new { error = "repository must be owner/name." });
        if (string.IsNullOrWhiteSpace(form.CommitSha) || form.CommitSha.Length < 7)
            return BadRequest(new { error = "commitSha is required (full SHA preferred)." });

        var repository = await ResolveAuthorizedRepository(form.Repository, cancellationToken);
        if (repository is null)
            return NotFound(new { error = $"Repository '{form.Repository}' is unknown here (is the GitHub App installed?) or the token doesn't grant it." });

        var commitId = Entities.Commit.DocumentId(repository.GitHubId, form.CommitSha);
        var commit = await session.LoadAsync<Commit>(commitId, cancellationToken);
        if (commit is null)
        {
            commit = new Commit { Sha = form.CommitSha, Repository = repository.Id };
            await session.StoreAsync(commit, commitId, cancellationToken);
        }
        commit.Branch ??= form.Branch;
        commit.PullRequestNumber ??= form.PullRequestNumber;
        commit.ParentSha ??= form.ParentSha;

        var buildId = Build.DocumentId(repository.GitHubId, form.CommitSha, form.RunId, form.RunAttempt);
        var build = await session.LoadAsync<Build>(buildId, cancellationToken);
        if (build is null)
        {
            build = new Build
            {
                Commit = commitId,
                CiRunId = form.RunId,
                CiRunAttempt = form.RunAttempt,
                WorkflowName = form.Workflow,
                EventName = form.EventName,
                CreatedAtUtc = DateTime.UtcNow,
            };
            await session.StoreAsync(build, buildId, cancellationToken);
        }
        else if (build.Status == "Finalized")
        {
            // A late upload re-opens the build; the finalizer will close it again
            // and recompute — max-merge keeps this correct.
            build.Status = "Open";
            build.FinalizedAtUtc = null;
            build.FinalizeReason = null;
            build.FinishRequested = false;
        }

        var sessionId = Guid.NewGuid().ToString("N")[..12];
        var buildSession = new BuildSession
        {
            SessionId = sessionId,
            JobName = form.JobName,
            Flags = (form.Flags ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            UploadedAtUtc = DateTime.UtcNow,
            RootDir = form.RootDir,
        };

        var attachmentNames = new List<string>();
        var index = 0;
        foreach (var file in form.Files)
        {
            var name = UploadAttachments.ReportName(sessionId, index++, file.FileName);
            session.Advanced.Attachments.Store(build, name, file.OpenReadStream());
            attachmentNames.Add(name);
        }
        if (!string.IsNullOrEmpty(form.FileList))
        {
            session.Advanced.Attachments.Store(build, UploadAttachments.FileListName(sessionId),
                new MemoryStream(System.Text.Encoding.UTF8.GetBytes(form.FileList)));
        }

        buildSession.RawFileNames = [.. attachmentNames];
        build.Sessions.Add(buildSession);
        build.LastUploadAtUtc = DateTime.UtcNow;

        await session.SaveChangesAsync(cancellationToken);
        await messageBus.BroadcastAsync(new ParseSessionMessage { BuildId = buildId, SessionId = sessionId }, cancellationToken);

        logger.LogInformation("Accepted upload for {Repo}@{Sha} run {RunId}.{Attempt} session {SessionId} ({Files} files)",
            form.Repository, form.CommitSha, form.RunId, form.RunAttempt, sessionId, form.Files.Count);

        return Accepted(new UploadResponse(buildId, sessionId));
    }

    /// <summary>Explicitly closes the run's build instead of waiting for the debounce.</summary>
    [HttpPost("finish")]
    public async Task<IActionResult> Finish([FromBody] FinishRequest request, CancellationToken cancellationToken)
    {
        var repository = await ResolveAuthorizedRepository(request.Repository, cancellationToken);
        if (repository is null)
            return NotFound();

        var buildId = Build.DocumentId(repository.GitHubId, request.CommitSha, request.RunId, request.RunAttempt);
        var build = await session.LoadAsync<Build>(buildId, cancellationToken);
        if (build is null)
            return NotFound();

        // Finalizing before every session is parsed would promote a stale
        // summary onto the commit — defer to the moment the last session lands.
        if (build.Sessions.Any(s => s.ParseStatus == "Pending"))
        {
            build.FinishRequested = true;
            await session.SaveChangesAsync(cancellationToken);
            return Accepted(new { status = "PendingParse" });
        }

        await BuildFinalizer.Finalize(session, build, "Explicit", cancellationToken);
        await session.SaveChangesAsync(cancellationToken);
        return Ok();
    }

    public sealed class UploadForm
    {
        public required string Repository { get; set; }
        public required string CommitSha { get; set; }
        public string? Branch { get; set; }
        public int? PullRequestNumber { get; set; }
        public string? ParentSha { get; set; }
        public long RunId { get; set; }
        public int RunAttempt { get; set; } = 1;
        public string? JobName { get; set; }
        public string? Workflow { get; set; }
        public string? EventName { get; set; }
        public string? Flags { get; set; }
        public string? RootDir { get; set; }
        public string? FileList { get; set; }
        public IFormFileCollection Files { get; set; } = new FormFileCollection();
    }

    public sealed record FinishRequest(string Repository, string CommitSha, long RunId, int RunAttempt);

    private async Task<Repository?> ResolveAuthorizedRepository(string fullName, CancellationToken cancellationToken)
    {
        var repository = await session.Query<Repository>()
            .Where(r => r.FullName == fullName)
            .FirstOrDefaultAsync(cancellationToken);
        if (repository is null)
            return null;

        var scope = User.FindFirst(ApiTokenAuthenticationHandler.ScopeClaim)?.Value;
        var account = User.FindFirst(ApiTokenAuthenticationHandler.AccountClaim)?.Value;
        var repoId = User.FindFirst(ApiTokenAuthenticationHandler.RepositoryClaim)?.Value;

        var authorized = scope switch
        {
            "Account" => string.Equals(account, repository.OwnerLogin, StringComparison.OrdinalIgnoreCase),
            "Repository" => repoId == repository.GitHubId.ToString(),
            _ => false,
        };

        // Unknown and unauthorized look identical to the caller (no existence leak).
        return authorized ? repository : null;
    }
}
