using Coverage.Entities;
using Coverage.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Raven.Client.Documents.Session;
using Raven.TestDriver;
using Xunit;

namespace Coverage.Tests.Services;

/// <summary>
/// The regression pin for the org-wide-credential hole: management used to gate
/// on installation *visibility*, which is owner-granular, so any org member —
/// including one with read-only access to a single repository — could mint an
/// account-scoped upload token covering the whole organization and revoke
/// everyone else's.
///
/// An account-scoped credential can upload for every repository of the account,
/// so the bar is administering every repository of the account. These cases pin
/// both directions, plus the vacuous-truth trap that would otherwise hand
/// whole-account authority to anyone at all.
/// </summary>
public class AccountAdminGateTests : CoverageRavenTest
{
    private const long AcmeAccount = 100;
    private const long PublicRepo = 1;
    private const long SecretRepo = 2;
    private const long ThirdRepo = 3;

    private static Repository Repo(long gitHubId, string name) => new()
    {
        GitHubId = gitHubId,
        Name = name,
        FullName = $"acme/{name}",
        OwnerLogin = "acme",
        OwnerGitHubId = AcmeAccount,
    };

    private static UserAccess Snapshot(params (long RepositoryGitHubId, RepositoryAccessLevel Level)[] levels) => new()
    {
        UserId = "SparkUsers/test",
        OwnerGitHubIds = [AcmeAccount],
        Repositories = [.. levels.Select(l => new RepositoryEntitlement
        {
            RepositoryGitHubId = l.RepositoryGitHubId,
            Level = l.Level,
        })],
    };

    private static IAccountAccessService CreateService(IAsyncDocumentSession session, UserAccess? access)
    {
        var services = new ServiceCollection();
        services.AddSingleton(session);
        services.AddSingleton<IUserAccessService>(new ScriptedUserAccessService(access));
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddScoped<IAccountAccessService, AccountAccessService>();
        return services.BuildServiceProvider().GetRequiredService<IAccountAccessService>();
    }

    private async Task<IAsyncDocumentSession> SeedAsync(params Repository[] repositories)
    {
        var store = GetDocumentStore();
        using (var seed = store.OpenAsyncSession())
        {
            foreach (var repository in repositories)
                await seed.StoreAsync(repository, Repository.DocumentId(repository.GitHubId));
            await seed.SaveChangesAsync();
        }
        WaitForIndexing(store);
        return store.OpenAsyncSession();
    }

    [Fact]
    public async Task Read_only_on_one_repository_cannot_mint_an_account_scoped_credential()
    {
        using var session = await SeedAsync(Repo(PublicRepo, "public-one"), Repo(SecretRepo, "secret"));
        var service = CreateService(session, Snapshot((PublicRepo, RepositoryAccessLevel.Read)));

        (await service.CanAdministerWholeAccountAsync(AcmeAccount)).Should().BeFalse(
            "this is exactly the principal that used to be able to mint an org-wide token");
        (await service.CanManageAnyRepositoryAsync(AcmeAccount)).Should().BeFalse(
            "read is not manage on any repository either");
    }

    [Fact]
    public async Task Admin_on_some_but_not_all_repositories_cannot_mint_an_account_scoped_credential()
    {
        using var session = await SeedAsync(
            Repo(PublicRepo, "public-one"), Repo(SecretRepo, "secret"), Repo(ThirdRepo, "third"));
        var service = CreateService(session, Snapshot(
            (PublicRepo, RepositoryAccessLevel.Admin),
            (SecretRepo, RepositoryAccessLevel.Admin),
            (ThirdRepo, RepositoryAccessLevel.Read)));

        (await service.CanAdministerWholeAccountAsync(AcmeAccount)).Should().BeFalse(
            "an account-scoped token would reach the repository they only read");
        (await service.CanManageAnyRepositoryAsync(AcmeAccount)).Should().BeTrue(
            "they do administer something here, so they may list the account's tokens");
    }

    [Fact]
    public async Task Admin_on_every_repository_may_mint_an_account_scoped_credential()
    {
        using var session = await SeedAsync(Repo(PublicRepo, "public-one"), Repo(SecretRepo, "secret"));
        var service = CreateService(session, Snapshot(
            (PublicRepo, RepositoryAccessLevel.Admin),
            (SecretRepo, RepositoryAccessLevel.Admin)));

        (await service.CanAdministerWholeAccountAsync(AcmeAccount)).Should().BeTrue();
    }

    /// <summary>
    /// <c>All()</c> over an empty set is vacuously true, so an account with no
    /// known repositories would otherwise grant whole-account authority to
    /// anybody — including a viewer with no entitlement at all.
    /// </summary>
    [Fact]
    public async Task An_account_with_no_known_repositories_grants_nothing()
    {
        using var session = await SeedAsync();
        var service = CreateService(session, Snapshot());

        (await service.CanAdministerWholeAccountAsync(AcmeAccount)).Should().BeFalse();
    }

    [Fact]
    public async Task An_anonymous_viewer_administers_nothing()
    {
        using var session = await SeedAsync(Repo(PublicRepo, "public-one"));
        var service = CreateService(session, access: null);

        (await service.CanAdministerWholeAccountAsync(AcmeAccount)).Should().BeFalse();
        (await service.CanManageAnyRepositoryAsync(AcmeAccount)).Should().BeFalse();
    }
}
