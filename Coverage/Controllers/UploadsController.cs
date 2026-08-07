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
[Authorize(AuthenticationSchemes = $"{ApiTokenAuthenticationHandler.SchemeName},{GitHubOidc.SchemeName}")]
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

        // OIDC claims are GitHub-signed and unforgeable — they override the
        // body's copies so a workflow can't attach its coverage to someone
        // else's run. The `sha` claim is NOT used: on pull_request events it
        // is the ephemeral merge commit, while the body carries the PR head.
        if (long.TryParse(User.FindFirst(GitHubOidc.RunIdClaim)?.Value, out var claimRunId))
            form.RunId = claimRunId;
        if (int.TryParse(User.FindFirst(GitHubOidc.RunAttemptClaim)?.Value, out var claimRunAttempt))
            form.RunAttempt = claimRunAttempt;

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

        // Finalization rides the parse queue (FIFO): it runs after every parse
        // enqueued before this call, so it can never promote a stale summary or
        // race a parse's save.
        await messageBus.BroadcastAsync(new FinalizeBuildMessage { BuildId = buildId }, cancellationToken);
        return Accepted(new { status = "Finalizing" });
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
        // OIDC path: the GitHub-signed `repository` claim IS the authorization —
        // a workflow can only ever upload for the repository it runs in.
        var oidcRepository = User.FindFirst(GitHubOidc.RepositoryClaim)?.Value;
        if (oidcRepository is not null)
        {
            if (!string.Equals(oidcRepository, fullName, StringComparison.OrdinalIgnoreCase))
                return null;
            return await ResolveOidcRepository(cancellationToken);
        }

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

    /// <summary>
    /// Loads the OIDC caller's repository; public repositories auto-provision on
    /// first upload (no App installation needed), private ones must already be
    /// known via the GitHub App.
    /// </summary>
    private async Task<Repository?> ResolveOidcRepository(CancellationToken cancellationToken)
    {
        if (!long.TryParse(User.FindFirst(GitHubOidc.RepositoryIdClaim)?.Value, out var gitHubRepoId))
            return null;

        var repository = await session.LoadAsync<Repository>(Repository.DocumentId(gitHubRepoId), cancellationToken);
        if (repository is not null)
            return repository;

        if (User.FindFirst(GitHubOidc.RepositoryVisibilityClaim)?.Value != "public")
            return null;

        var fullName = User.FindFirst(GitHubOidc.RepositoryClaim)!.Value;
        var ownerLogin = User.FindFirst(GitHubOidc.RepositoryOwnerClaim)?.Value ?? fullName.Split('/')[0];

        Account? account = null;
        if (long.TryParse(User.FindFirst(GitHubOidc.RepositoryOwnerIdClaim)?.Value, out var ownerId))
        {
            account = await session.LoadAsync<Account>(Account.DocumentId(ownerId), cancellationToken);
            if (account is null)
            {
                account = new Account { GitHubId = ownerId, Login = ownerLogin };
                await session.StoreAsync(account, Account.DocumentId(ownerId), cancellationToken);
            }
        }

        repository = new Repository
        {
            GitHubId = gitHubRepoId,
            Account = account?.Id,
            Name = fullName.Split('/')[1],
            FullName = fullName,
            OwnerLogin = ownerLogin,
            IsPrivate = false,
        };
        await session.StoreAsync(repository, Repository.DocumentId(gitHubRepoId), cancellationToken);
        logger.LogInformation("Auto-provisioned public repository {FullName} from OIDC upload", fullName);
        return repository;
    }
}
