using Coverage.Entities;
using Coverage.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MintPlayer.SourceGenerators.Attributes;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;

namespace Coverage.Controllers;

[ApiController]
[Route("api/me")]
[Authorize]
public partial class MeController : ControllerBase
{
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly IGitHubAccessService gitHubAccess;
    [Inject] private readonly IUserAccessService userAccess;
    [Inject] private readonly IConfiguration configuration;
    [Inject] private readonly IWebHostEnvironment environment;

    /// <summary>
    /// The accounts (user + organizations) the signed-in user may see, joined
    /// with what we know about them (App installed or not) and an aggregate of
    /// their repositories' latest coverage. Carries the environment's GitHub
    /// App public page (GitHub:{env}:AppSlug, defaulting to the well-known
    /// per-environment slug) so "install the App" links point at the right App.
    /// </summary>
    [HttpGet("accounts")]
    public async Task<ActionResult<AccountsResponse>> GetAccounts(CancellationToken cancellationToken)
    {
        var appSlug = configuration[$"GitHub:{environment.EnvironmentName}:AppSlug"];
        if (string.IsNullOrEmpty(appSlug))
            appSlug = environment.IsDevelopment() ? "coveragedevelopment" : "coverageproduction";
        var appUrl = $"https://github.com/apps/{appSlug}";

        var visibility = await gitHubAccess.GetVisibilityAsync(cancellationToken);
        var owners = visibility.OwnerGitHubIds;
        // Reauth travels as a flag on a 200 — the SPA's auth interceptor
        // hijacks any non-/spark/auth 401 into a full /login navigation.
        var reauthRequired = visibility.TokenState == GitHubTokenState.ReauthRequired;
        if (owners.Length == 0)
            return Ok(new AccountsResponse(appUrl, [], reauthRequired));

        var known = await session.Query<Account, Indexes.Accounts_Overview>()
            .Where(a => a.GitHubId.In(owners))
            .ToListAsync(cancellationToken);

        var repos = await session.Query<Repository, Indexes.Repositories_Overview>()
            .Where(r => r.OwnerGitHubId.In(owners))
            .Take(4096)
            .ToListAsync(cancellationToken);
        var reposByOwner = repos.ToLookup(r => r.OwnerGitHubId);

        var byGitHubId = known.ToDictionary(a => a.GitHubId);

        var result = owners
            .Select(owner =>
            {
                var ownerRepos = reposByOwner[owner].ToList();
                var covered = ownerRepos.Sum(r => r.LatestCoverage?.LinesCovered ?? 0);
                var coverable = ownerRepos.Sum(r => r.LatestCoverage?.LinesCoverable ?? 0);
                var aggregate = coverable > 0 ? Math.Round(covered * 100.0 / coverable, 1) : (double?)null;
                // An owner GitHub gave us but we have no Account document for
                // yet still needs a row; its login is unknown until the next
                // backfill, so fall back to the first repo that names it.
                return byGitHubId.TryGetValue(owner, out var account)
                    ? new AccountInfo(account.Login, account.Type, account.AvatarUrl, account.InstallationId is not null, ownerRepos.Count, aggregate)
                    : new AccountInfo(ownerRepos.FirstOrDefault()?.OwnerLogin ?? owner.ToString(), "User", null, false, ownerRepos.Count, aggregate);
            })
            .OrderBy(a => a.Login, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return Ok(new AccountsResponse(appUrl, result, reauthRequired));
    }

    /// <summary>
    /// Drops the cached GitHub visibility for the signed-in user and returns
    /// the freshly queried account list (manual counterpart of the 5-min TTL).
    /// </summary>
    [HttpPost("accounts/resync")]
    public async Task<ActionResult<AccountsResponse>> Resync(CancellationToken cancellationToken)
    {
        await gitHubAccess.InvalidateAsync(cancellationToken);
        // The persisted snapshot is the authority for per-repository access, so a
        // resync that only cleared the owner cache would leave the thing the user
        // is actually complaining about untouched.
        await userAccess.InvalidateAsync(cancellationToken);
        return await GetAccounts(cancellationToken);
    }

    /// <param name="GitHubReauthRequired">The stored GitHub token is dead and silent refresh
    /// failed — only a browser round-trip (the "Reconnect GitHub" button) can fix it. While
    /// set, <paramref name="Accounts"/> is degraded to the user's own account.</param>
    public sealed record AccountsResponse(string GitHubAppUrl, AccountInfo[] Accounts, bool GitHubReauthRequired = false);
    public sealed record AccountInfo(string Login, string Type, string? AvatarUrl, bool Installed, int RepoCount, double? AggregateCoverage);
}
