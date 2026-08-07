using Coverage.Badges;
using Coverage.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using MintPlayer.SourceGenerators.Attributes;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;

namespace Coverage.Controllers;

/// <summary>
/// README badges. Public repos render unauthenticated; private repos require
/// the repo's badge token — a capability that grants ONLY this SVG, never
/// report data. Wrong/missing token renders "unknown" (never 404: a 404 would
/// confirm the repo exists).
/// </summary>
[ApiController]
[AllowAnonymous]
[EnableRateLimiting("uploads")]
public partial class BadgeController : ControllerBase
{
    [Inject] private readonly IAsyncDocumentSession session;

    [HttpGet("badge/{owner}/{name}.svg")]
    [ResponseCache(Duration = 300, Location = ResponseCacheLocation.Any)]
    public async Task<IActionResult> Get(string owner, string name, [FromQuery] string? token, CancellationToken cancellationToken)
    {
        var repository = await session.Query<Repository>()
            .Where(r => r.FullName == $"{owner}/{name}")
            .FirstOrDefaultAsync(cancellationToken);

        double? percent = null;
        if (repository is not null && MayView(repository, token) && repository.LatestCoverage is { LinesCoverable: > 0 } coverage)
        {
            percent = coverage.LinesCovered * 100.0 / coverage.LinesCoverable;
        }

        Response.Headers.CacheControl = repository?.IsPrivate == true
            ? "private, max-age=300"
            : "public, max-age=300";

        return Content(BadgeRenderer.Coverage(percent), "image/svg+xml; charset=utf-8");
    }

    private static bool MayView(Repository repository, string? token)
    {
        if (!repository.IsPrivate) return true;
        if (string.IsNullOrEmpty(repository.BadgeToken) || string.IsNullOrEmpty(token)) return false;
        // Constant-time compare: the badge token is a credential, however narrow.
        var expected = System.Text.Encoding.UTF8.GetBytes(repository.BadgeToken);
        var presented = System.Text.Encoding.UTF8.GetBytes(token);
        return expected.Length == presented.Length
            && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(expected, presented);
    }
}
