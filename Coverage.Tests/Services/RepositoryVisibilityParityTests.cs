using Coverage.Entities;
using Coverage.Indexes;
using Coverage.Services;
using FluentAssertions;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Coverage.Tests;
using Raven.TestDriver;
using Xunit;

namespace Coverage.Tests.Services;

/// <summary>
/// Two anonymous read surfaces serve the same documents — the <c>/api/browse</c>
/// controllers and Spark's generic <c>/spark</c> query API — and each decides
/// visibility its own way: one imperatively on a loaded document, one as a
/// RavenDB row filter. They have to agree forever, and until now a doc-comment
/// was the only thing saying so.
///
/// This pins them together. The next visibility concept (an org allowlist,
/// private-but-shared, an unlisted state) breaks this test unless it lands in
/// both, which is the point.
/// </summary>
public class RepositoryVisibilityParityTests : CoverageRavenTest
{
    private const long Acme = 100;
    private const long Globex = 200;
    private const long Initech = 300;

    // Repository GitHub ids from the corpus below. Entitlement is per
    // repository, not per owner: reaching an organization's installation must
    // not grant every repository that organization owns.
    private const long AcmeSecret = 2;
    private const long GlobexHidden = 4;
    private const long InitechConfidential = 5;
    private const long UnknownRepository = 999;

    private static Repository Repo(long id, long ownerId, string owner, string name, bool isPrivate) => new()
    {
        GitHubId = id,
        Name = name,
        FullName = $"{owner}/{name}",
        OwnerLogin = owner,
        OwnerGitHubId = ownerId,
        IsPrivate = isPrivate,
    };

    private static readonly Repository[] Corpus =
    [
        Repo(1, Acme, "acme", "public-one", isPrivate: false),
        Repo(2, Acme, "acme", "secret", isPrivate: true),
        Repo(3, Globex, "globex", "public-two", isPrivate: false),
        Repo(4, Globex, "globex", "hidden", isPrivate: true),
        Repo(5, Initech, "initech", "confidential", isPrivate: true),
    ];

    private async Task<IDocumentStore> SeedCorpus()
    {
        var store = GetDocumentStore();
        using var session = store.OpenAsyncSession();
        foreach (var repository in Corpus)
            await session.StoreAsync(repository, Repository.DocumentId(repository.GitHubId));
        await session.SaveChangesAsync();
        WaitForIndexing(store);
        return store;
    }

    [Theory]
    // Anonymous: the filter must reduce to "public only".
    [InlineData(new long[0], new[] { "acme/public-one", "globex/public-two" })]
    // One entitled private repository adds exactly that one.
    [InlineData(new[] { AcmeSecret }, new[] { "acme/public-one", "acme/secret", "globex/public-two" })]
    [InlineData(new[] { AcmeSecret, InitechConfidential },
        new[] { "acme/public-one", "acme/secret", "globex/public-two", "initech/confidential" })]
    // A repository we know nothing about grants nothing.
    [InlineData(new[] { UnknownRepository }, new[] { "acme/public-one", "globex/public-two" })]
    // The regression that motivated per-repository entitlement: being entitled
    // to one repository of an owner must NOT surface that owner's siblings.
    // Under the old owner-granular rule, AcmeSecret would have dragged in every
    // private repository acme owns.
    [InlineData(new[] { GlobexHidden },
        new[] { "acme/public-one", "globex/hidden", "globex/public-two" })]
    public async Task Both_surfaces_resolve_the_same_repositories_for_the_same_principal(
        long[] entitledRepositoryIds, string[] expected)
    {
        using var store = await SeedCorpus();
        using var session = store.OpenAsyncSession();

        // The /spark surface: a row filter pushed down to RavenDB.
        var throughRowFilter = await session.Query<Repository, Repositories_Overview>()
            .Where(RepositoryVisibility.Filter(entitledRepositoryIds))
            .ToListAsync();

        // The /api/browse surface: the same rule evaluated on a loaded document.
        var throughImperativeCheck = Corpus.Where(r => RepositoryVisibility.IsVisible(r, entitledRepositoryIds));

        throughRowFilter.Select(r => r.FullName).Should().BeEquivalentTo(expected);
        throughImperativeCheck.Select(r => r.FullName).Should().BeEquivalentTo(expected);
    }

    /// <summary>
    /// The reason the rule keys on the owner's numeric id rather than their
    /// login. A rename rewrites OwnerLogin and FullName on every repository of
    /// that account; entitlement must not notice. Keyed on the login, the
    /// viewer's allow-list would stop matching and every private repository of
    /// the renamed org would silently vanish from their view — and a stale
    /// local account holding the freed login would gain it instead.
    ///
    /// This case previously asserted case-insensitive login matching, which is
    /// a question the id key no longer has to answer.
    /// </summary>
    [Fact]
    public async Task Renaming_an_owner_does_not_change_what_its_members_may_see()
    {
        using var store = await SeedCorpus();
        long[] allowed = [AcmeSecret];

        using (var session = store.OpenAsyncSession())
        {
            var before = await session.Query<Repository, Repositories_Overview>()
                .Where(RepositoryVisibility.Filter(allowed))
                .ToListAsync();
            before.Select(r => r.FullName).Should().Contain("acme/secret");

            // The rename: same account id, new login, rewritten full names.
            foreach (var repository in await session.Query<Repository, Repositories_Overview>()
                         .Where(r => r.OwnerGitHubId == Acme).ToListAsync())
            {
                repository.OwnerLogin = "acme-industries";
                repository.FullName = $"acme-industries/{repository.Name}";
            }
            await session.SaveChangesAsync();
        }
        WaitForIndexing(store);

        using (var session = store.OpenAsyncSession())
        {
            var after = await session.Query<Repository, Repositories_Overview>()
                .Where(RepositoryVisibility.Filter(allowed))
                .ToListAsync();

            after.Select(r => r.FullName).Should().Contain("acme-industries/secret",
                "the private repository stays visible to the same principal across a rename");
            after.Should().HaveCount(3);
        }
    }
}
