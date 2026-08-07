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

    /// <summary>
    /// The accounts (user + organizations) the signed-in user may see, joined
    /// with what we know about them (App installed or not).
    /// </summary>
    [HttpGet("accounts")]
    public async Task<ActionResult<IEnumerable<AccountInfo>>> GetAccounts(CancellationToken cancellationToken)
    {
        var owners = await gitHubAccess.GetAllowedOwnersAsync(cancellationToken);
        if (owners.Length == 0)
            return Ok(Array.Empty<AccountInfo>());

        var known = await session.Query<Account>()
            .Where(a => a.Login.In(owners))
            .ToListAsync(cancellationToken);

        var byLogin = known.ToDictionary(a => a.Login, StringComparer.OrdinalIgnoreCase);

        var result = owners
            .Select(owner => byLogin.TryGetValue(owner, out var account)
                ? new AccountInfo(account.Login, account.Type, account.AvatarUrl, account.InstallationId is not null)
                : new AccountInfo(owner, "User", null, false))
            .OrderBy(a => a.Login, StringComparer.OrdinalIgnoreCase);

        return Ok(result);
    }

    public sealed record AccountInfo(string Login, string Type, string? AvatarUrl, bool Installed);
}
