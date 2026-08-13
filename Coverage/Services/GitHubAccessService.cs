using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using Coverage.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Caching.Memory;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Authorization.Identity;
using Raven.Client.Documents.Session;

namespace Coverage.Services;

[Register(typeof(IGitHubAccessService), ServiceLifetime.Scoped)]
public partial class GitHubAccessService : IGitHubAccessService
{
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;
    [Inject] private readonly UserManager<SparkUser> userManager;
    [Inject] private readonly IHttpClientFactory httpClientFactory;
    [Inject] private readonly IMemoryCache memoryCache;
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly ILogger<GitHubAccessService> logger;

    private const string GitHubLoginProvider = "GitHub";
    private const string AccessTokenName = "access_token";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

    public async Task<string[]> GetAllowedOwnersAsync(CancellationToken cancellationToken = default)
    {
        var principal = httpContextAccessor.HttpContext?.User;
        if (principal?.Identity?.IsAuthenticated != true)
            return [];

        var user = await userManager.GetUserAsync(principal);
        if (user is null)
            return [];

        var cacheKey = $"github-owners/{user.Id}";
        if (memoryCache.TryGetValue<string[]>(cacheKey, out var cached) && cached is not null)
            return cached;

        var accessToken = await userManager.GetAuthenticationTokenAsync(user, GitHubLoginProvider, AccessTokenName);
        if (string.IsNullOrEmpty(accessToken))
        {
            logger.LogWarning("No GitHub access token found for user {UserId}", user.Id);
            return [];
        }

        var installations = await QueryGitHubInstallationsAsync(accessToken, cancellationToken);
        await BackfillInstallationIdsAsync(installations, cancellationToken);
        var username = principal.FindFirstValue(ClaimTypes.Name);

        var owners = installations
            .Select(i => i.Login)
            .Concat(username is not null ? [username] : [])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        memoryCache.Set(cacheKey, owners, CacheDuration);
        return owners;
    }

    public async Task<bool> IsOwnerAllowedAsync(string ownerLogin, CancellationToken cancellationToken = default)
    {
        var owners = await GetAllowedOwnersAsync(cancellationToken);
        return owners.Contains(ownerLogin, StringComparer.OrdinalIgnoreCase);
    }

    public async Task InvalidateAsync(CancellationToken cancellationToken = default)
    {
        var principal = httpContextAccessor.HttpContext?.User;
        if (principal?.Identity?.IsAuthenticated != true)
            return;

        var user = await userManager.GetUserAsync(principal);
        if (user is not null)
            memoryCache.Remove($"github-owners/{user.Id}");
    }

    private async Task<GitHubInstallation[]> QueryGitHubInstallationsAsync(string accessToken, CancellationToken cancellationToken)
    {
        try
        {
            var httpClient = httpClientFactory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user/installations");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Coverage", "1.0"));

            var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("GitHub /user/installations query failed: {StatusCode}", response.StatusCode);
                return [];
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            return ParseInstallations(json);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to query GitHub installations");
            return [];
        }
    }

    public static GitHubInstallation[] ParseInstallations(string json)
    {
        using var doc = JsonDocument.Parse(json);

        if (!doc.RootElement.TryGetProperty("installations", out var installations))
            return [];

        var result = new List<GitHubInstallation>();
        foreach (var installation in installations.EnumerateArray())
        {
            if (!installation.TryGetProperty("id", out var idNode) || !idNode.TryGetInt64(out var installationId)) continue;
            if (!installation.TryGetProperty("account", out var account)) continue;
            if (account.ValueKind != JsonValueKind.Object) continue;
            if (!account.TryGetProperty("id", out var accountIdNode) || !accountIdNode.TryGetInt64(out var accountId)) continue;
            if (!account.TryGetProperty("login", out var loginNode)) continue;
            var login = loginNode.GetString();
            if (string.IsNullOrEmpty(login)) continue;

            var type = installation.TryGetProperty("target_type", out var typeNode) ? typeNode.GetString() : null;
            var avatarUrl = account.TryGetProperty("avatar_url", out var avatarNode) ? avatarNode.GetString() : null;
            var suspended = installation.TryGetProperty("suspended_at", out var suspendedNode)
                && suspendedNode.ValueKind != JsonValueKind.Null;

            result.Add(new GitHubInstallation(installationId, accountId, login, type, avatarUrl, suspended));
        }
        return [.. result];
    }

    /// <summary>
    /// GET /user/installations already carries the current installation id per
    /// account — persist it, because the `installation` webhook is the only
    /// other writer and GitHub never redelivers a lost one (which left the
    /// "App installed" badge permanently grey). Only sets/corrects ids;
    /// clearing on uninstall stays the webhook's job — an installation absent
    /// from THIS user's response may simply be invisible to them.
    /// </summary>
    private async Task BackfillInstallationIdsAsync(GitHubInstallation[] installations, CancellationToken cancellationToken)
    {
        var active = installations.Where(i => !i.Suspended).ToArray();
        if (active.Length == 0)
            return;

        try
        {
            var loaded = await session.LoadAsync<Account>(
                active.Select(i => Account.DocumentId(i.AccountGitHubId)), cancellationToken);

            foreach (var installation in active)
            {
                var id = Account.DocumentId(installation.AccountGitHubId);
                var account = loaded.GetValueOrDefault(id);
                if (account is null)
                {
                    account = new Account
                    {
                        GitHubId = installation.AccountGitHubId,
                        Login = installation.Login,
                        Type = installation.Type == "Organization" ? "Organization" : "User",
                        AvatarUrl = installation.AvatarUrl,
                    };
                    await session.StoreAsync(account, id, cancellationToken);
                }
                account.InstallationId = installation.Id;
            }

            if (session.Advanced.HasChanges)
            {
                // The caller immediately queries Accounts by Login; wait for
                // indexing so a just-created account shows up in that query
                // (existing accounts are unaffected — their index entry is
                // already there and documents load fresh).
                session.Advanced.WaitForIndexesAfterSaveChanges(TimeSpan.FromSeconds(5), throwOnTimeout: false);
                await session.SaveChangesAsync(cancellationToken);
            }
        }
        catch (Exception ex)
        {
            // Visibility must never depend on the backfill.
            logger.LogError(ex, "Failed to backfill installation ids");
        }
    }
}

/// <summary>One entry of GET /user/installations, reduced to what we consume.</summary>
public sealed record GitHubInstallation(long Id, long AccountGitHubId, string Login, string? Type, string? AvatarUrl, bool Suspended);
