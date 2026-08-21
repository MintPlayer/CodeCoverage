using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Coverage.Entities;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Webhooks.GitHub.Services;

namespace Coverage.Services;

/// <summary>
/// Reads entitlement from GitHub's REST API. Every method fails toward "could
/// not ask" (null) rather than toward an empty answer, because an empty answer
/// is indistinguishable from a real revocation and would silently hide data.
/// </summary>
[Register(typeof(IGitHubEntitlementSource), ServiceLifetime.Scoped)]
public partial class GitHubEntitlementSource : IGitHubEntitlementSource
{
    [Inject] private readonly IHttpClientFactory httpClientFactory;
    [Inject] private readonly IGitHubInstallationService installationService;
    [Inject] private readonly ILogger<GitHubEntitlementSource> logger;

    /// <summary>GitHub's maximum, so a 1000-repo org costs 10 calls rather than 100.</summary>
    private const int PageSize = 100;

    /// <summary>A runaway guard, not a real limit: 100 pages is 10,000 repositories.</summary>
    private const int MaxPages = 100;

    public async Task<EntitlementSnapshot?> FetchAsync(string accessToken, CancellationToken cancellationToken = default)
    {
        var installationsJson = await GetAsync("https://api.github.com/user/installations", accessToken, cancellationToken);
        if (installationsJson is null)
        {
            logger.LogWarning("Entitlement fetch: /user/installations did not answer; keeping any existing snapshot");
            return null;
        }

        var installations = GitHubAccessService.ParseInstallations(installationsJson)
            .Where(i => !i.Suspended)
            .ToArray();

        var repositories = new List<RepositoryEntitlement>();
        var owners = new List<long>();

        foreach (var installation in installations)
        {
            var forInstallation = await FetchInstallationRepositoriesAsync(installation.Id, accessToken, cancellationToken);
            if (forInstallation is null)
            {
                // One installation failing must not silently narrow the whole
                // snapshot — that would read as a revocation. Abandon the build.
                logger.LogWarning(
                    "Entitlement fetch: installation {InstallationId} ({Login}) did not answer; abandoning this rebuild",
                    installation.Id, installation.Login);
                return null;
            }

            owners.Add(installation.AccountGitHubId);
            repositories.AddRange(forInstallation);
            logger.LogInformation(
                "Entitlement fetch: installation {InstallationId} ({Login}) granted {Count} repositories",
                installation.Id, installation.Login, forInstallation.Count);
        }

        return new EntitlementSnapshot([.. owners.Distinct()], repositories);
    }

    private async Task<List<RepositoryEntitlement>?> FetchInstallationRepositoriesAsync(
        long installationId, string accessToken, CancellationToken cancellationToken)
    {
        var result = new List<RepositoryEntitlement>();

        for (var page = 1; page <= MaxPages; page++)
        {
            var json = await GetAsync(
                $"https://api.github.com/user/installations/{installationId}/repositories?per_page={PageSize}&page={page}",
                accessToken, cancellationToken);
            if (json is null) return null;

            var (batch, total) = ParseInstallationRepositories(json);
            result.AddRange(batch);

            if (batch.Count < PageSize || result.Count >= total)
                return result;
        }

        logger.LogWarning(
            "Entitlement fetch: installation {InstallationId} exceeded {MaxPages} pages; truncating at {Count} repositories",
            installationId, MaxPages, result.Count);
        return result;
    }

    /// <summary>
    /// <c>{ total_count, repositories: [ { id, permissions: { admin, push, pull } } ] }</c>.
    /// A repository whose permissions say neither pull nor push nor admin is
    /// dropped rather than stored as None — absence already means denial, and
    /// storing it would only grow the document.
    /// </summary>
    public static (List<RepositoryEntitlement> Batch, int Total) ParseInstallationRepositories(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var total = root.TryGetProperty("total_count", out var totalNode) && totalNode.TryGetInt32(out var parsed)
            ? parsed
            : int.MaxValue;

        var batch = new List<RepositoryEntitlement>();
        if (!root.TryGetProperty("repositories", out var repositories) || repositories.ValueKind != JsonValueKind.Array)
            return (batch, total);

        foreach (var repository in repositories.EnumerateArray())
        {
            if (!repository.TryGetProperty("id", out var idNode) || !idNode.TryGetInt64(out var gitHubId))
                continue;

            var level = LevelFrom(repository);
            if (level == RepositoryAccessLevel.None)
                continue;

            batch.Add(new RepositoryEntitlement { RepositoryGitHubId = gitHubId, Level = level });
        }

        return (batch, total);
    }

    /// <summary>
    /// <c>admin</c> is the bar for management, deliberately not <c>push</c>: it
    /// errs toward refusing, and the reverse mistake is a disclosure. If it ever
    /// locks out maintainers who wire CI without admin rights, relaxing
    /// repository-scoped token minting to <c>push</c> is a one-line change here.
    /// </summary>
    private static RepositoryAccessLevel LevelFrom(JsonElement repository)
    {
        if (!repository.TryGetProperty("permissions", out var permissions) || permissions.ValueKind != JsonValueKind.Object)
            // No permissions hash at all: the endpoint only returns repositories
            // the user can reach, so treat presence as read rather than dropping it.
            return RepositoryAccessLevel.Read;

        if (Flag(permissions, "admin")) return RepositoryAccessLevel.Admin;
        if (Flag(permissions, "push") || Flag(permissions, "pull") || Flag(permissions, "maintain")
            || Flag(permissions, "triage"))
            return RepositoryAccessLevel.Read;

        return RepositoryAccessLevel.None;

        static bool Flag(JsonElement permissions, string name)
            => permissions.TryGetProperty(name, out var node)
                && node.ValueKind == JsonValueKind.True;
    }

    public async Task<RepositoryAccessLevel?> FetchOneAsync(
        string accessToken, string fullName, CancellationToken cancellationToken = default)
    {
        var response = await SendAsync($"https://api.github.com/repos/{fullName}", accessToken, cancellationToken);
        if (response is null) return null;

        using (response)
        {
            // GitHub does not distinguish "private and forbidden" from "does not
            // exist", which is exactly the semantic we want: both are None.
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                logger.LogInformation("Live entitlement check: {FullName} is 404 for this user — no access", fullName);
                return RepositoryAccessLevel.None;
            }
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Live entitlement check: {FullName} returned {StatusCode}", fullName, response.StatusCode);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(json);
            var level = LevelFrom(document.RootElement);
            logger.LogInformation("Live entitlement check: {FullName} resolves to {Level}", fullName, level);
            return level;
        }
    }

    public async Task<bool?> IsPubliclyVisibleAsync(
        string fullName, long? installationId, CancellationToken cancellationToken = default)
    {
        if (installationId is not null)
        {
            // Authoritative: the App has access, so a 200 carries the true
            // `private` value rather than merely proving public readability.
            try
            {
                var client = await installationService.CreateInstallationClientAsync(installationId.Value);
                var parts = fullName.Split('/', 2);
                var repository = await client.Repository.Get(parts[0], parts[1]);
                logger.LogInformation("Visibility refresh: {FullName} via installation {InstallationId} — private={Private}",
                    fullName, installationId, repository.Private);
                return !repository.Private;
            }
            catch (Octokit.NotFoundException)
            {
                // The App lost access. Not provably public any more, so fail closed.
                logger.LogWarning("Visibility refresh: {FullName} is 404 for installation {InstallationId} — treating as private",
                    fullName, installationId);
                return false;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Visibility refresh: installation read failed for {FullName}", fullName);
                return null;
            }
        }

        // No installation (an OIDC-provisioned repository). An unauthenticated
        // read proves public readability and nothing else, which is all this
        // question needs — but it is metered at 60/hr per IP, so the lease has to
        // be long and this must never run per request.
        var response = await SendAsync($"https://api.github.com/repos/{fullName}", accessToken: null, cancellationToken);
        if (response is null) return null;

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                logger.LogInformation("Visibility refresh: {FullName} is 404 unauthenticated — no longer publicly readable", fullName);
                return false;
            }
            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                // Almost certainly the unauthenticated rate limit, which is not
                // evidence about the repository. Keep the old value.
                logger.LogWarning("Visibility refresh: {FullName} returned 403 (rate limit?); leaving the lease alone", fullName);
                return null;
            }
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Visibility refresh: {FullName} returned {StatusCode}", fullName, response.StatusCode);
                return null;
            }

            logger.LogInformation("Visibility refresh: {FullName} is publicly readable", fullName);
            return true;
        }
    }

    private async Task<string?> GetAsync(string url, string? accessToken, CancellationToken cancellationToken)
    {
        var response = await SendAsync(url, accessToken, cancellationToken);
        if (response is null) return null;

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("GitHub GET {Url} failed: {StatusCode}", url, response.StatusCode);
                return null;
            }
            return await response.Content.ReadAsStringAsync(cancellationToken);
        }
    }

    private async Task<HttpResponseMessage?> SendAsync(string url, string? accessToken, CancellationToken cancellationToken)
    {
        try
        {
            var httpClient = httpClientFactory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (accessToken is not null)
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Coverage", "1.0"));

            return await httpClient.SendAsync(request, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "GitHub GET {Url} threw", url);
            return null;
        }
    }
}
