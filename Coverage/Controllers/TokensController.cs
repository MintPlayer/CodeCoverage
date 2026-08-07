using Coverage.ApiTokens;
using Coverage.Entities;
using Coverage.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Authorization.Identity;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;

namespace Coverage.Controllers;

/// <summary>
/// Upload-token management for the signed-in user (cookie auth + XSRF via the
/// Spark middleware). Whether a user may manage an account's tokens mirrors
/// their GitHub visibility of that account.
/// </summary>
[ApiController]
[Route("api/tokens")]
[Authorize]
public partial class TokensController : ControllerBase
{
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly IGitHubAccessService gitHubAccess;
    [Inject] private readonly UserManager<SparkUser> userManager;

    public sealed record CreateTokenRequest(string AccountLogin, string? Description);
    public sealed record CreatedToken(string TokenValue, string AccountLogin, string? Description);
    public sealed record TokenInfo(string Id, string AccountLogin, string? Description, DateTime CreatedAtUtc, DateTime? RevokedAtUtc);

    [HttpPost]
    public async Task<ActionResult<CreatedToken>> Create([FromBody] CreateTokenRequest request, CancellationToken cancellationToken)
    {
        if (!await gitHubAccess.IsOwnerAllowedAsync(request.AccountLogin, cancellationToken))
            return Forbid();

        var user = await userManager.GetUserAsync(User);
        if (user is null) return Unauthorized();

        var tokenValue = ApiTokenService.GenerateTokenValue();
        var token = new ApiToken
        {
            Scope = "Account",
            AccountLogin = request.AccountLogin,
            Description = request.Description,
            CreatedByUserId = user.Id!,
            CreatedAtUtc = DateTime.UtcNow,
        };
        await session.StoreAsync(token, ApiToken.DocumentId(ApiTokenService.Hash(tokenValue)), cancellationToken);
        await session.SaveChangesAsync(cancellationToken);

        // The plaintext value exists only in this response.
        return Ok(new CreatedToken(tokenValue, request.AccountLogin, request.Description));
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<TokenInfo>>> List([FromQuery] string account, CancellationToken cancellationToken)
    {
        if (!await gitHubAccess.IsOwnerAllowedAsync(account, cancellationToken))
            return Forbid();

        var tokens = await session.Query<ApiToken>()
            .Where(t => t.AccountLogin == account)
            .ToListAsync(cancellationToken);

        return Ok(tokens.Select(t => new TokenInfo(t.Id!, t.AccountLogin!, t.Description, t.CreatedAtUtc, t.RevokedAtUtc)));
    }

    [HttpDelete("{hash}")]
    public async Task<IActionResult> Revoke(string hash, CancellationToken cancellationToken)
    {
        var token = await session.LoadAsync<ApiToken>(ApiToken.DocumentId(hash), cancellationToken);
        if (token is null) return NotFound();

        if (token.AccountLogin is null || !await gitHubAccess.IsOwnerAllowedAsync(token.AccountLogin, cancellationToken))
            return Forbid();

        token.RevokedAtUtc = DateTime.UtcNow;
        await session.SaveChangesAsync(cancellationToken);
        return NoContent();
    }
}
