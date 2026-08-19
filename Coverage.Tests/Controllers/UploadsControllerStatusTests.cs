using System.Security.Claims;
using Coverage.ApiTokens;
using Coverage.Controllers;
using Coverage.Entities;
using Coverage.Indexes;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Messaging.Abstractions;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Session;
using Coverage.Tests;
using Raven.TestDriver;
using Xunit;

namespace Coverage.Tests.Controllers;

/// <summary>
/// GET /api/uploads/status — the endpoint a CI gate polls. What is pinned here
/// is the contract in docs/upload-api.md: the three <c>state</c> values, the two
/// distinguishable 404s, that a baseline never compares a build against itself,
/// and that a read never writes.
/// </summary>
public class UploadsControllerStatusTests : CoverageRavenTest
{
    private const long RepoId = 4242;
    private const string RepoName = "acme/widgets";
    private const string Sha = "1111111111111111111111111111111111111111";
    private const string BaselineSha = "2222222222222222222222222222222222222222";

    /// <summary>Status is a pure read; the controller only takes the bus for its POST paths.</summary>
    private sealed class NullMessageBus : IMessageBus
    {
        public Task BroadcastAsync<TMessage>(TMessage message, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task BroadcastAsync<TMessage>(TMessage message, string queueName, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task DelayBroadcastAsync<TMessage>(TMessage message, TimeSpan delay, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    /// <summary>An upload-token principal scoped to the whole account.</summary>
    private static ClaimsPrincipal AccountToken(string owner) => new(new ClaimsIdentity(
        [
            new Claim(ApiTokenAuthenticationHandler.ScopeClaim, "Account"),
            new Claim(ApiTokenAuthenticationHandler.AccountClaim, owner),
        ], ApiTokenAuthenticationHandler.SchemeName));

    /// <summary>A GitHub Actions OIDC principal for a public repository.</summary>
    private static ClaimsPrincipal OidcToken(string fullName, long repositoryId) => new(new ClaimsIdentity(
        [
            new Claim(GitHubOidc.RepositoryClaim, fullName),
            new Claim(GitHubOidc.RepositoryIdClaim, repositoryId.ToString()),
            new Claim(GitHubOidc.RepositoryOwnerClaim, fullName.Split('/')[0]),
            new Claim(GitHubOidc.RepositoryVisibilityClaim, "public"),
        ], GitHubOidc.SchemeName));

    private static UploadsController CreateController(IAsyncDocumentSession session, ClaimsPrincipal user)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(session);
        services.AddSingleton<IMessageBus>(new NullMessageBus());
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Coverage:BaseUrl"] = "https://coverage.example.com" })
            .Build());
        services.AddScoped<UploadsController>();

        var controller = services.BuildServiceProvider().GetRequiredService<UploadsController>();
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = user },
        };
        return controller;
    }

    /// <summary>A private repository, so the OIDC auto-provision path can't reach it either.</summary>
    private static async Task SeedRepository(IAsyncDocumentSession session, string defaultBranch = "master")
    {
        await session.StoreAsync(new Repository
        {
            GitHubId = RepoId,
            Name = "widgets",
            FullName = RepoName,
            OwnerLogin = "acme",
            IsPrivate = true,
            DefaultBranch = defaultBranch,
        }, Repository.DocumentId(RepoId), default);
    }

    private static async Task<Build> SeedBuild(
        IAsyncDocumentSession session, string sha, long runId, string buildStatus,
        string? finalizeReason, CoverageSummary? coverage, params string[] sessionStatuses)
    {
        var commitId = Commit.DocumentId(RepoId, sha);
        if (await session.LoadAsync<Commit>(commitId) is null)
        {
            await session.StoreAsync(new Commit
            {
                Sha = sha,
                Repository = Repository.DocumentId(RepoId),
                Branch = "master",
                FirstSeenAtUtc = DateTimeOffset.UtcNow,
            }, commitId, default);
        }

        var build = new Build
        {
            Commit = commitId,
            CiRunId = runId,
            CiRunAttempt = 1,
            Status = buildStatus,
            FinalizeReason = finalizeReason,
            CreatedAtUtc = DateTime.UtcNow,
            Coverage = coverage,
            Sessions = [.. sessionStatuses.Select((status, i) => new BuildSession
            {
                SessionId = $"session{i}",
                ParseStatus = status,
                Error = status == "Failed" ? "boom" : null,
                FilesCount = status == "Parsed" ? 12 : 0,
            })],
        };
        await session.StoreAsync(build, Build.DocumentId(RepoId, sha, runId, 1), default);
        return build;
    }

    private static UploadsController.UploadStatusResponse Body(ActionResult<UploadsController.UploadStatusResponse> result)
        => (UploadsController.UploadStatusResponse)((OkObjectResult)result.Result!).Value!;

    // ---- state classification -------------------------------------------------
    // Pure, so they need no database: this is the contract every consumer branches on.

    [Fact]
    public void Open_build_is_InFlight()
        => Build.ClassifyState(new Build { Status = "Open" }).Should().Be("InFlight");

    [Fact]
    public void Finalized_build_with_a_pending_session_is_still_InFlight()
        => Build.ClassifyState(new Build
        {
            Status = "Finalized",
            Sessions = [new() { ParseStatus = "Parsed" }, new() { ParseStatus = "Pending" }],
        }).Should().Be("InFlight");

    [Fact]
    public void Finalized_and_all_parsed_is_Complete()
        => Build.ClassifyState(new Build
        {
            Status = "Finalized",
            FinalizeReason = "Explicit",
            Sessions = [new() { ParseStatus = "Parsed" }, new() { ParseStatus = "Parsed" }],
        }).Should().Be("Complete");

    [Fact]
    public void One_failed_session_makes_it_CompleteWithErrors()
        => Build.ClassifyState(new Build
        {
            Status = "Finalized",
            FinalizeReason = "Debounce",
            Sessions = [new() { ParseStatus = "Parsed" }, new() { ParseStatus = "Failed" }],
        }).Should().Be("CompleteWithErrors");

    [Fact]
    public void Timeout_is_CompleteWithErrors_even_if_every_session_reads_parsed()
        => Build.ClassifyState(new Build
        {
            Status = "Finalized",
            FinalizeReason = "Timeout",
            Sessions = [new() { ParseStatus = "Parsed" }],
        }).Should().Be("CompleteWithErrors");

    /// <summary>
    /// T1.2 will add a partial-parse status. It must land in CompleteWithErrors
    /// without anyone touching this classifier or any consumer — that is the
    /// whole reason `state` is derived rather than exposed raw.
    /// </summary>
    [Fact]
    public void A_future_terminal_parse_status_is_absorbed_into_CompleteWithErrors()
        => Build.ClassifyState(new Build
        {
            Status = "Finalized",
            FinalizeReason = "Debounce",
            Sessions = [new() { ParseStatus = "Parsed" }, new() { ParseStatus = "Partial" }],
        }).Should().Be("CompleteWithErrors");

    // ---- the endpoint ---------------------------------------------------------

    [Fact]
    public async Task Reports_InFlight_then_Complete_across_a_builds_life()
    {
        using var store = GetDocumentStore();
        using var session = store.OpenAsyncSession();
        await SeedRepository(session);
        await SeedBuild(session, Sha, runId: 7, "Open", null, null, "Pending");
        await session.SaveChangesAsync();
        WaitForIndexing(store);

        var controller = CreateController(session, AccountToken("acme"));

        var inFlight = Body(await controller.Status(RepoName, Sha, runId: 7));
        inFlight.State.Should().Be("InFlight");
        inFlight.Coverage.Should().BeNull();
        inFlight.CommitUrl.Should().Be($"https://coverage.example.com/r/{RepoName}/c/{Sha}");

        var build = await session.LoadAsync<Build>(Build.DocumentId(RepoId, Sha, 7, 1));
        build.Status = "Finalized";
        build.FinalizeReason = "Explicit";
        build.Sessions[0].ParseStatus = "Parsed";
        build.Coverage = new CoverageSummary { LinesCovered = 80, LinesCoverable = 100 };
        await session.SaveChangesAsync();
        WaitForIndexing(store);

        var complete = Body(await controller.Status(RepoName, Sha, runId: 7));
        complete.State.Should().Be("Complete");
        complete.Status.Should().Be("Finalized");
        complete.FinalizeReason.Should().Be("Explicit");
        complete.Coverage!.LinesCovered.Should().Be(80);
        complete.Sessions.Should().ContainSingle().Which.ParseStatus.Should().Be("Parsed");
    }

    [Fact]
    public async Task A_failed_session_surfaces_as_CompleteWithErrors_with_its_error()
    {
        using var store = GetDocumentStore();
        using var session = store.OpenAsyncSession();
        await SeedRepository(session);
        await SeedBuild(session, Sha, 7, "Finalized", "Debounce",
            new CoverageSummary { LinesCovered = 40, LinesCoverable = 100 }, "Parsed", "Failed");
        await session.SaveChangesAsync();
        WaitForIndexing(store);

        var response = Body(await CreateController(session, AccountToken("acme")).Status(RepoName, Sha, 7));

        response.State.Should().Be("CompleteWithErrors");
        // The number is still returned — it is real, it just under-counts.
        response.Coverage!.LinesCovered.Should().Be(40);
        response.Sessions.Should().Contain(s => s.ParseStatus == "Failed" && s.Error == "boom");
    }

    [Fact]
    public async Task A_timed_out_build_is_CompleteWithErrors()
    {
        using var store = GetDocumentStore();
        using var session = store.OpenAsyncSession();
        await SeedRepository(session);
        await SeedBuild(session, Sha, 7, "Finalized", "Timeout",
            new CoverageSummary { LinesCovered = 10, LinesCoverable = 100 }, "Failed");
        await session.SaveChangesAsync();
        WaitForIndexing(store);

        var response = Body(await CreateController(session, AccountToken("acme")).Status(RepoName, Sha, 7));

        response.State.Should().Be("CompleteWithErrors");
        response.FinalizeReason.Should().Be("Timeout");
    }

    // ---- the baseline ---------------------------------------------------------

    /// <summary>
    /// The trap the endpoint exists to avoid: Repository.LatestCoverage holds
    /// this very build's number by the time the build is terminal, so a baseline
    /// read from there would compare a commit against itself and every ratchet
    /// would pass. The baseline must be the *previous* default-branch commit.
    /// </summary>
    [Fact]
    public async Task Baseline_is_the_previous_default_branch_commit_never_the_polled_one()
    {
        using var store = GetDocumentStore();
        using var session = store.OpenAsyncSession();
        await SeedRepository(session);

        await SeedBuild(session, BaselineSha, 6, "Finalized", "Explicit",
            new CoverageSummary { LinesCovered = 70, LinesCoverable = 100 }, "Parsed");
        await SeedBuild(session, Sha, 7, "Finalized", "Explicit",
            new CoverageSummary { LinesCovered = 90, LinesCoverable = 100 }, "Parsed");
        await session.SaveChangesAsync();
        WaitForIndexing(store);

        // Both commits carry finalized coverage, as they would after finalize.
        var older = await session.LoadAsync<Commit>(Commit.DocumentId(RepoId, BaselineSha));
        older.Coverage = new CoverageSummary { LinesCovered = 70, LinesCoverable = 100 };
        older.AuthoredAt = DateTimeOffset.UtcNow.AddHours(-1);
        var newer = await session.LoadAsync<Commit>(Commit.DocumentId(RepoId, Sha));
        newer.Coverage = new CoverageSummary { LinesCovered = 90, LinesCoverable = 100 };
        newer.AuthoredAt = DateTimeOffset.UtcNow;
        await session.SaveChangesAsync();
        WaitForIndexing(store);

        var response = Body(await CreateController(session, AccountToken("acme")).Status(RepoName, Sha, 7));

        response.Baseline.Should().NotBeNull();
        response.Baseline!.Sha.Should().Be(BaselineSha);
        response.Baseline.Coverage!.LinesCovered.Should().Be(70);
    }

    [Fact]
    public async Task Baseline_is_null_on_a_first_upload_so_a_ratchet_passes()
    {
        using var store = GetDocumentStore();
        using var session = store.OpenAsyncSession();
        await SeedRepository(session);
        await SeedBuild(session, Sha, 7, "Finalized", "Explicit",
            new CoverageSummary { LinesCovered = 90, LinesCoverable = 100 }, "Parsed");
        await session.SaveChangesAsync();
        WaitForIndexing(store);

        var response = Body(await CreateController(session, AccountToken("acme")).Status(RepoName, Sha, 7));

        response.Baseline.Should().BeNull();
    }

    // ---- authorization and the two 404s ---------------------------------------

    [Fact]
    public async Task An_unknown_repository_and_an_unauthorized_one_are_indistinguishable()
    {
        using var store = GetDocumentStore();
        using var session = store.OpenAsyncSession();
        await SeedRepository(session);
        await SeedBuild(session, Sha, 7, "Finalized", "Explicit", null, "Parsed");
        await session.SaveChangesAsync();
        WaitForIndexing(store);

        // A token for a different account: the repository exists, but saying so
        // would leak the existence of a private repository.
        var unauthorized = await CreateController(session, AccountToken("someone-else")).Status(RepoName, Sha, 7);
        var unknown = await CreateController(session, AccountToken("acme")).Status("acme/nope", Sha, 7);

        foreach (var result in new[] { unauthorized, unknown })
        {
            var notFound = result.Result.Should().BeOfType<NotFoundResult>().Subject;
            notFound.StatusCode.Should().Be(404);
        }
    }

    /// <summary>
    /// Once authorization is proven, "no build for this run" stops being a
    /// secret and starts being useful — it means the caller asked the wrong
    /// question (a mismatched runId), not "keep polling".
    /// </summary>
    [Fact]
    public async Task An_unknown_run_on_an_authorized_repository_says_so()
    {
        using var store = GetDocumentStore();
        using var session = store.OpenAsyncSession();
        await SeedRepository(session);
        await session.SaveChangesAsync();
        WaitForIndexing(store);

        var result = await CreateController(session, AccountToken("acme")).Status(RepoName, Sha, runId: 999);

        var notFound = result.Result.Should().BeOfType<NotFoundObjectResult>().Subject;
        notFound.Value!.ToString().Should().Contain("No build for run 999");
    }

    /// <summary>
    /// The upload path auto-provisions a public repository from an OIDC claim,
    /// because registering it is part of what an upload means. A poll must not:
    /// reading is not registering, and a mistyped poll would otherwise leave a
    /// permanent empty repository behind.
    /// </summary>
    [Fact]
    public async Task An_oidc_poll_for_an_unknown_repository_stores_nothing()
    {
        using var store = GetDocumentStore();
        using var session = store.OpenAsyncSession();

        var result = await CreateController(session, OidcToken("acme/brand-new", 999_000))
            .Status("acme/brand-new", Sha, 7);

        result.Result.Should().BeOfType<NotFoundResult>();

        using var probe = store.OpenAsyncSession();
        (await probe.LoadAsync<Repository>(Repository.DocumentId(999_000))).Should().BeNull();
    }

    [Fact]
    public async Task Rejects_a_repository_that_is_not_owner_slash_name()
    {
        using var store = GetDocumentStore();
        using var session = store.OpenAsyncSession();

        var result = await CreateController(session, AccountToken("acme")).Status("widgets", Sha, 7);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }
}
