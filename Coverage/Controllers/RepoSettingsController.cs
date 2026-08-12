using System.Security.Cryptography;
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
[Route("api/repos/{owner}/{name}/settings")]
[Authorize]
public partial class RepoSettingsController : ControllerBase
{
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly IGitHubAccessService gitHubAccess;

    /// <summary>
    /// (Re)generates the badge token. Rotation invalidates the previous badge
    /// URL immediately; upload tokens are untouched.
    /// </summary>
    [HttpPost("badge-token")]
    public async Task<ActionResult<object>> RotateBadgeToken(string owner, string name, CancellationToken cancellationToken)
    {
        var repository = await session.Query<Repository>()
            .Where(r => r.FullName == $"{owner}/{name}")
            .FirstOrDefaultAsync(cancellationToken);
        if (repository is null) return NotFound();

        if (!await gitHubAccess.IsOwnerAllowedAsync(repository.OwnerLogin, cancellationToken))
            return NotFound();

        repository.BadgeToken = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        await session.SaveChangesAsync(cancellationToken);

        return Ok(new { badgeToken = repository.BadgeToken });
    }
}
