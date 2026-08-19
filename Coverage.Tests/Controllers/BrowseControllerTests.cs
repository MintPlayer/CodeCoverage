using System.Text.Json;
using Coverage.Controllers;
using Coverage.Entities;
using Coverage.Services;
using Coverage.Tests.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;
using Raven.TestDriver;
using Xunit;

namespace Coverage.Tests.Controllers;

public class BrowseControllerTests : CoverageRavenTest
{
    private sealed class NullContentService : IGitHubContentService
    {
        public Task<string?> GetFileContentAsync(Repository repository, long? installationId, string sha, string path, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);
    }

    private static BrowseController CreateController(IAsyncDocumentSession session)
    {
        var services = new ServiceCollection();
        services.AddSingleton(session);
        services.AddSingleton<IGitHubAccessService>(new ScriptedAccessService(new([], GitHubTokenState.Ok)));
        services.AddSingleton<IGitHubContentService>(new NullContentService());
        services.AddSingleton(GitHubAuthTestFakes.TestConfiguration());
        services.AddScoped<BrowseController>();
        return services.BuildServiceProvider().GetRequiredService<BrowseController>();
    }

    /// <summary>
    /// Regression for the phantom-builds bug: FileCoverage
    /// (<c>{buildId}/files/{hash}</c>) and BuildTreeSummary
    /// (<c>{buildId}/tree</c>) ids nest under the same <c>…/builds/</c> prefix
    /// the build stream scans, and each one deserialized into an all-default
    /// Build — the commit page showed one "0.0 / Jan 1, year 1" row per
    /// covered file (~1,760 on a real ng-bootstrap commit).
    /// </summary>
    [Fact]
    public async Task GetCommit_returns_only_real_builds_not_file_and_tree_documents_sharing_the_prefix()
    {
        using var store = GetDocumentStore();
        const long repoId = 1;
        const string sha = "79bc284939350991803acc84ced894ade844b9f0";
        var buildId = Build.DocumentId(repoId, sha, runId: 42, runAttempt: 1);

        using (var seed = store.OpenAsyncSession())
        {
            await seed.StoreAsync(new Repository
            {
                GitHubId = repoId,
                Name = "repo",
                FullName = "owner/repo",
                OwnerLogin = "owner",
                IsPrivate = false,
            }, Repository.DocumentId(repoId));
            await seed.StoreAsync(new Commit { Sha = sha, Repository = Repository.DocumentId(repoId) },
                Commit.DocumentId(repoId, sha));
            await seed.StoreAsync(new Build
            {
                Commit = Commit.DocumentId(repoId, sha),
                CiRunId = 42,
                CiRunAttempt = 1,
                Status = "Finalized",
                CreatedAtUtc = DateTime.UtcNow,
            }, buildId);

            // The neighbors that share the /builds/ prefix and must NOT
            // surface as builds — one per covered file, plus the tree summary.
            for (var i = 0; i < 25; i++)
            {
                await seed.StoreAsync(new FileCoverage
                {
                    BuildId = buildId,
                    Path = $"src/file{i}.cs",
                    Matched = true,
                }, FileCoverage.DocumentId(buildId, $"src/file{i}.cs"));
            }
            await seed.StoreAsync(new BuildTreeSummary { BuildId = buildId }, BuildTreeSummary.DocumentId(buildId));

            await seed.SaveChangesAsync();
        }
        WaitForIndexing(store); // ResolveVisibleRepository queries by FullName

        using var session = store.OpenAsyncSession();
        var result = await CreateController(session).GetCommit("owner", "repo", sha, CancellationToken.None);

        var payload = JsonDocument.Parse(JsonSerializer.Serialize(((OkObjectResult)result.Result!).Value));
        var builds = payload.RootElement.GetProperty("Builds").EnumerateArray().ToList();

        builds.Should().ContainSingle("only the actual Build document is a direct child of /builds/");
        builds[0].GetProperty("RunId").GetInt64().Should().Be(42);
        builds[0].GetProperty("RunAttempt").GetInt32().Should().Be(1);
    }

    /// <summary>
    /// #13 U3: the unmatched-files list is a capped sample, and the UI used to
    /// render the sample's length as if it were the total (314 unmatched files
    /// read as 50). The response must carry the real count alongside the sample.
    /// </summary>
    [Fact]
    public async Task GetTree_discloses_the_real_unmatched_count_alongside_the_capped_sample()
    {
        using var store = GetDocumentStore();
        const long repoId = 2;
        const string sha = "aa11284939350991803acc84ced894ade844b9f0";
        var buildId = Build.DocumentId(repoId, sha, runId: 7, runAttempt: 1);

        using (var seed = store.OpenAsyncSession())
        {
            await seed.StoreAsync(new Repository
            {
                GitHubId = repoId,
                Name = "repo",
                FullName = "owner/repo",
                OwnerLogin = "owner",
                IsPrivate = false,
            }, Repository.DocumentId(repoId));
            await seed.StoreAsync(new Commit
            {
                Sha = sha,
                Repository = Repository.DocumentId(repoId),
                LatestBuildId = buildId,
            }, Commit.DocumentId(repoId, sha));

            var files = new List<TreeFileSummary>
            {
                new() { Path = "src/matched.ts", Matched = true, LinesCovered = 1, LinesCoverable = 2 },
            };
            for (var i = 0; i < 314; i++)
            {
                files.Add(new TreeFileSummary { Path = $"lib{i}/index.ts", Matched = false });
            }
            await seed.StoreAsync(new BuildTreeSummary { BuildId = buildId, Files = files },
                BuildTreeSummary.DocumentId(buildId));

            await seed.SaveChangesAsync();
        }
        WaitForIndexing(store); // ResolveVisibleRepository queries by FullName

        using var session = store.OpenAsyncSession();
        var controller = CreateController(session);

        var root = (BrowseController.TreeResponse)((OkObjectResult)
            (await controller.GetTree("owner", "repo", sha, path: null, flag: null, CancellationToken.None)).Result!).Value!;
        root.UnmatchedFiles.Should().HaveCount(50, "the sample stays capped");
        root.UnmatchedTotal.Should().Be(314, "the real count must be disclosed");

        // Subfolder responses carry no unmatched info at all today; the total
        // must agree with the (empty) sample rather than leak the root count.
        var sub = (BrowseController.TreeResponse)((OkObjectResult)
            (await controller.GetTree("owner", "repo", sha, path: "src", flag: null, CancellationToken.None)).Result!).Value!;
        sub.UnmatchedFiles.Should().BeEmpty();
        sub.UnmatchedTotal.Should().Be(0);
    }
}
