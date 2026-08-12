using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Caching.Memory;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Authorization.Identity;

namespace Coverage.Services;

[Register(typeof(IGitHubAccessService), ServiceLifetime.Scoped)]
public partial class GitHubAccessService : IGitHubAccessService
{
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;
    [Inject] private readonly UserManager<SparkUser> userManager;
    [Inject] private readonly IHttpClientFactory httpClientFactory;
    [Inject] private readonly IMemoryCache memoryCache;
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

        var installationOwners = await QueryGitHubInstallationOwnersAsync(accessToken, cancellationToken);
        var username = principal.FindFirstValue(ClaimTypes.Name);

        var owners = installationOwners
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

    private async Task<string[]> QueryGitHubInstallationOwnersAsync(string accessToken, CancellationToken cancellationToken)
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
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("installations", out var installations))
                return [];

            var result = new List<string>();
            foreach (var installation in installations.EnumerateArray())
            {
                if (!installation.TryGetProperty("account", out var account)) continue;
                if (account.ValueKind == JsonValueKind.Null) continue;
                if (!account.TryGetProperty("login", out var loginNode)) continue;
                var login = loginNode.GetString();
                if (!string.IsNullOrEmpty(login))
                    result.Add(login);
            }
            return [.. result];
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to query GitHub installations");
            return [];
        }
    }
}
