using Coverage.Controllers;
using Coverage.Services;
using Coverage.Tests.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Raven.Client.Documents.Session;
using Coverage.Tests;
using Raven.TestDriver;
using Xunit;

namespace Coverage.Tests.Controllers;

/// <summary>
/// The reauth flag travels as a field on a 200 response — never as a status
/// code, because the SPA's auth interceptor hijacks 401s into /login.
/// </summary>
public class MeControllerTests : CoverageRavenTest
{
    private const long OwnGitHubId = 1234567;

    /// <summary>
    /// Visibility is a set of GitHub ids, so the degraded row can only carry a
    /// login if the Account document is there to supply it.
    /// </summary>
    private static async Task SeedOwnAccount(IAsyncDocumentSession session)
    {
        await session.StoreAsync(
            new Coverage.Entities.Account { GitHubId = OwnGitHubId, Login = "pieterjan", Type = "User" },
            Coverage.Entities.Account.DocumentId(OwnGitHubId));
        await session.SaveChangesAsync();
    }

    private static MeController CreateController(IAsyncDocumentSession session, GitHubVisibility visibility)
    {
        var services = new ServiceCollection();
        services.AddSingleton(session);
        services.AddSingleton<IGitHubAccessService>(new ScriptedAccessService(visibility));
        services.AddSingleton(GitHubAuthTestFakes.TestConfiguration());
        services.AddSingleton<IWebHostEnvironment>(new FakeWebHostEnvironment());
        services.AddScoped<MeController>();
        return services.BuildServiceProvider().GetRequiredService<MeController>();
    }

    private static MeController.AccountsResponse Body(ActionResult<MeController.AccountsResponse> result)
        => (MeController.AccountsResponse)((OkObjectResult)result.Result!).Value!;

    [Fact]
    public async Task Reauth_required_visibility_sets_the_flag_and_still_lists_the_degraded_account()
    {
        using var store = GetDocumentStore();
        using var session = store.OpenAsyncSession();
        await SeedOwnAccount(session);
        WaitForIndexing(store);
        var controller = CreateController(session, new([OwnGitHubId], GitHubTokenState.ReauthRequired));

        var response = Body(await controller.GetAccounts(CancellationToken.None));

        response.GitHubReauthRequired.Should().BeTrue();
        response.Accounts.Should().ContainSingle().Which.Login.Should().Be("pieterjan");
    }

    [Fact]
    public async Task Healthy_visibility_reports_no_reauth()
    {
        using var store = GetDocumentStore();
        using var session = store.OpenAsyncSession();
        var controller = CreateController(session, new([OwnGitHubId], GitHubTokenState.Ok));

        var response = Body(await controller.GetAccounts(CancellationToken.None));

        response.GitHubReauthRequired.Should().BeFalse();
    }

    [Fact]
    public async Task Unavailable_is_not_reauth__transient_failure_must_not_summon_the_reconnect_banner()
    {
        using var store = GetDocumentStore();
        using var session = store.OpenAsyncSession();
        var controller = CreateController(session, new([OwnGitHubId], GitHubTokenState.Unavailable));

        var response = Body(await controller.GetAccounts(CancellationToken.None));

        response.GitHubReauthRequired.Should().BeFalse();
    }
}
