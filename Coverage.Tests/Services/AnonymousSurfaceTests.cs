using Coverage.Actions;
using Coverage.Entities;
using Coverage.Indexes;
using Coverage.Services;
using FluentAssertions;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.TestDriver;
using Xunit;

namespace Coverage.Tests.Services;

/// <summary>
/// What each class of caller may see on the generic /spark surface.
///
/// This exists because <c>security.json</c> grants <c>QueryRead</c> to the
/// <c>Everyone</c> group, which includes anonymous callers — so the row filters
/// and redaction sets are *the only thing* between the public internet and these
/// collections. Spark's own row-security guide says exactly that, and names this
/// repository as the example. There is no second gate behind them, and a rule
/// that fails open discloses every row **silently**: row-level denial here is a
/// filtered-out row, not an error.
///
/// Three findings went unnoticed for precisely that reason — every account was
/// enumerable, the gate policy was world-readable, and CI internals sat on the
/// anonymous build grid. So the anonymous case is asserted rather than assumed.
///
/// The Actions classes are thin wrappers over <see cref="AccountVisibility"/>,
/// <see cref="RepositoryVisibility"/> and the published redaction sets, so this
/// exercises those directly rather than standing up the Spark action pipeline —
/// the same approach as <c>RepositoryVisibilityParityTests</c>.
/// </summary>
public class AnonymousSurfaceTests : CoverageRavenTest
{
    private const long AcmeAccount = 100;
    private const long GhostAccount = 200;

    private const long AcmePublic = 1;
    private const long AcmeSecret = 2;
    private const long GhostSecret = 3;

    private static Account NewAcme() => new()
    {
        GitHubId = AcmeAccount, Login = "acme", Type = "Organization",
        InstallationId = 555, PublicRepoCount = 1,
    };

    /// <summary>An account that exists only because it owns a private repository.</summary>
    private static Account NewGhost() => new()
    {
        GitHubId = GhostAccount, Login = "ghost", Type = "Organization",
        InstallationId = 666, PublicRepoCount = 0,
    };

    private static Repository Repo(long gitHubId, long ownerId, string owner, string name, bool isPrivate) => new()
    {
        GitHubId = gitHubId,
        Name = name,
        FullName = owner + "/" + name,
        OwnerLogin = owner,
        OwnerGitHubId = ownerId,
        IsPrivate = isPrivate,
        BadgeToken = "badge-secret",
        Gate = new GateSettings { Blocking = true },
    };

    private async Task<IDocumentStore> SeedAsync()
    {
        var store = GetDocumentStore();
        using (var session = store.OpenAsyncSession())
        {
            await session.StoreAsync(NewAcme(), Account.DocumentId(AcmeAccount));
            await session.StoreAsync(NewGhost(), Account.DocumentId(GhostAccount));
            await session.StoreAsync(Repo(AcmePublic, AcmeAccount, "acme", "public-one", false), Repository.DocumentId(AcmePublic));
            await session.StoreAsync(Repo(AcmeSecret, AcmeAccount, "acme", "secret", true), Repository.DocumentId(AcmeSecret));
            await session.StoreAsync(Repo(GhostSecret, GhostAccount, "ghost", "secret", true), Repository.DocumentId(GhostSecret));
            await session.SaveChangesAsync();
        }
        WaitForIndexing(store);
        return store;
    }

    // The four callers the matrix distinguishes. "Outside collaborator" is the
    // principal that motivated per-repository entitlement: entitled to one public
    // repository of acme and nothing else.
    private static ISparkVisibility Anonymous() => new ScriptedSparkVisibility();

    private static ISparkVisibility OutsideCollaborator() => new ScriptedSparkVisibility(
        entitledRepositoryIds: [AcmePublic], allowedOwnerIds: [AcmeAccount]);

    private static ISparkVisibility Member() => new ScriptedSparkVisibility(
        entitledRepositoryIds: [AcmePublic, AcmeSecret], allowedOwnerIds: [AcmeAccount]);

    private static ISparkVisibility Administrator() => new ScriptedSparkVisibility(
        entitledRepositoryIds: [AcmePublic, AcmeSecret],
        administeredRepositoryIds: [AcmePublic, AcmeSecret],
        allowedOwnerIds: [AcmeAccount]);

    private static async Task<string[]> AccountsVisibleTo(ISparkVisibility visibility, IDocumentStore store)
    {
        using var session = store.OpenAsyncSession();
        var logins = await session.Query<Account, Accounts_Overview>()
            .Where(AccountVisibility.Filter(await visibility.GetAllowedOwnerIdsAsync()))
            .Select(a => a.Login)
            .ToListAsync();
        return [.. logins];
    }

    private static async Task<string[]> RepositoriesVisibleTo(ISparkVisibility visibility, IDocumentStore store)
    {
        using var session = store.OpenAsyncSession();
        var names = await session.Query<Repository, Repositories_Overview>()
            .Where(RepositoryVisibility.Filter(await visibility.GetEntitledRepositoryGitHubIdsAsync()))
            .Select(r => r.FullName)
            .ToListAsync();
        return [.. names];
    }

    [Fact]
    public async Task Anonymous_callers_cannot_enumerate_an_account_that_owns_only_private_repositories()
    {
        using var store = await SeedAsync();

        var visible = await AccountsVisibleTo(Anonymous(), store);

        visible.Should().BeEquivalentTo(["acme"],
            "ghost exists only because it owns a private repository, so listing it discloses "
            + "that the organization uses this service");
    }

    [Fact]
    public async Task A_member_sees_their_own_account_even_with_nothing_public()
    {
        using var store = await SeedAsync();
        var ghostMember = new ScriptedSparkVisibility(
            entitledRepositoryIds: [GhostSecret], allowedOwnerIds: [GhostAccount]);

        var visible = await AccountsVisibleTo(ghostMember, store);

        visible.Should().BeEquivalentTo(["acme", "ghost"]);
    }

    [Fact]
    public async Task Anonymous_and_non_member_callers_see_only_public_repositories()
    {
        using var store = await SeedAsync();

        (await RepositoriesVisibleTo(Anonymous(), store)).Should().BeEquivalentTo(["acme/public-one"]);

        (await RepositoriesVisibleTo(OutsideCollaborator(), store)).Should().BeEquivalentTo(["acme/public-one"],
            "an outside collaborator on one public repository must not reach its private siblings");
    }

    [Fact]
    public async Task A_member_reaches_exactly_the_private_repositories_they_are_entitled_to()
    {
        using var store = await SeedAsync();

        (await RepositoriesVisibleTo(Member(), store)).Should().BeEquivalentTo(["acme/public-one", "acme/secret"],
            "and never ghost/secret, which belongs to an account they have no entitlement in");
    }

    [Fact]
    public async Task Only_a_repository_administrator_may_receive_the_badge_token_and_the_gate_policy()
    {
        RepositoryActions.AdministratorOnlyAttributes.Should().BeEquivalentTo(
            [nameof(Repository.BadgeToken), nameof(Repository.Gate)],
            "BadgeToken is a credential and Gate is owner-facing CI policy");

        (await Anonymous().CanManageRepositoryAsync(AcmePublic)).Should().BeFalse();
        (await OutsideCollaborator().CanManageRepositoryAsync(AcmePublic)).Should().BeFalse();
        (await Member().CanManageRepositoryAsync(AcmePublic)).Should().BeFalse(
            "read access to a repository is not authority over it");
        (await Administrator().CanManageRepositoryAsync(AcmePublic)).Should().BeTrue();
    }

    [Fact]
    public async Task Only_an_account_manager_may_receive_the_installation_id()
    {
        AccountActions.ManagerOnlyAttributes.Should().BeEquivalentTo([nameof(Account.InstallationId)]);

        (await Anonymous().CanManageAccountAsync(AcmeAccount)).Should().BeFalse();
        (await Member().CanManageAccountAsync(AcmeAccount)).Should().BeFalse();
        (await Administrator().CanManageAccountAsync(AcmeAccount)).Should().BeTrue();
    }

    /// <summary>
    /// A build carries the runner's absolute workspace path, the uploaded file
    /// names, the job name, parse error text, the gate snapshot and check-run ids.
    /// All of it reached anonymous callers — on the grid as well as the detail
    /// view, because Sessions is a query column — and none of it is needed to read
    /// a coverage percentage.
    /// </summary>
    [Fact]
    public void Ci_internals_are_withheld_from_anyone_who_does_not_administer_the_repository()
    {
        BuildActions.AdministratorOnlyAttributes.Should().BeEquivalentTo([
            nameof(Build.Sessions) + "." + nameof(BuildSession.RootDir),
            nameof(Build.Sessions) + "." + nameof(BuildSession.RawFileNames),
            nameof(Build.Sessions) + "." + nameof(BuildSession.JobName),
            nameof(Build.Sessions) + "." + nameof(BuildSession.Error),
            nameof(Build.GateSnapshot),
            nameof(Build.Feedback),
            nameof(Build.DeclaredBaseSha),
        ]);
    }
}
