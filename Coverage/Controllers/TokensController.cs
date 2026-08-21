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
/// Spark middleware).
///
/// The bar matches the reach of the credential, never mere visibility of the
/// account: a repository-scoped token needs Admin on that repository, an
/// account-scoped one needs Admin across every repository of the account, and
/// listing needs Admin on at least one. Visibility used to be the whole gate,
/// which meant any org member -- read-only on a single repository included --
/// could mint an org-wide upload credential and revoke everyone else's.
/// </summary>
[ApiController]
[Route("api/tokens")]
[Authorize]
public partial class TokensController : ControllerBase
{
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly IAccountAccessService accountAccess;
    [Inject] private readonly IRepositoryAccessService repositoryAccess;
    [Inject] private readonly UserManager<SparkUser> userManager;

    public sealed record CreateTokenRequest(string AccountLogin, string? Description, string? Scope, string? RepositoryFullName);
    public sealed record CreatedToken(string TokenValue, string AccountLogin, string? Description, string Scope, string? RepositoryFullName);
    public sealed record TokenInfo(string Id, string AccountLogin, string? Description, string Scope, string? RepositoryFullName,
        DateTime CreatedAtUtc, DateTime? RevokedAtUtc);

    private async Task<bool> CanAdministerRepositoryAsync(long repositoryGitHubId, CancellationToken cancellationToken)
    {
        var repository = await session.LoadAsync<Repository>(
            Repository.DocumentId(repositoryGitHubId), cancellationToken);
        return repository is not null
            && await repositoryAccess.GetAsync(repository, cancellationToken) >= RepositoryAccessLevel.Admin;
    }

    private async Task<Account?> ResolveAccount(string login, CancellationToken cancellationToken)
        => await session.Query<Account, Indexes.Accounts_Overview>()
            .Where(a => a.Login == login)
            .FirstOrDefaultAsync(cancellationToken);

    [HttpPost]
    public async Task<ActionResult<CreatedToken>> Create([FromBody] CreateTokenRequest request, CancellationToken cancellationToken)
    {
        // The wire contract names the owner by login (that is what the UI has);
        // entitlement and storage both key on the account's immutable GitHub id.
        var account = await ResolveAccount(request.AccountLogin, cancellationToken);
        if (account is null)
            return NotFound(new { error = $"Account '{request.AccountLogin}' is unknown here." });

        var user = await userManager.GetUserAsync(User);
        if (user is null) return Unauthorized();

        // A repo-scoped token still records the owning account so it shows up in
        // that account's token list; the upload handler authorizes on Scope alone.
        Repository? repository = null;
        if (request.Scope == "Repository")
        {
            if (string.IsNullOrWhiteSpace(request.RepositoryFullName))
                return BadRequest(new { error = "repositoryFullName is required for a repository-scoped token." });
            repository = await session.Query<Repository, Indexes.Repositories_Overview>()
                .Where(r => r.FullName == request.RepositoryFullName)
                .FirstOrDefaultAsync(cancellationToken);
            if (repository is null || repository.OwnerGitHubId != account.GitHubId)
                return NotFound(new { error = $"Repository '{request.RepositoryFullName}' is unknown here or not owned by {request.AccountLogin}." });

            // A repository-scoped token uploads for exactly this repository, so
            // administering it is the whole requirement.
            if (await repositoryAccess.GetAsync(repository, cancellationToken) < RepositoryAccessLevel.Admin)
                return Forbid();
        }
        else if (request.Scope is not (null or "Account"))
        {
            return BadRequest(new { error = "scope must be Account or Repository." });
        }
        else if (!await accountAccess.CanAdministerWholeAccountAsync(account.GitHubId, cancellationToken))
        {
            // An account-scoped token can upload for every repository of the
            // account, so minting one requires administering every repository of
            // the account. This is the gate that used to be mere installation
            // visibility, which let any org member -- including read-only on a
            // single repository -- mint an org-wide credential and revoke
            // everyone else's.
            return Forbid();
        }

        var tokenValue = ApiTokenService.GenerateTokenValue();
        var token = new ApiToken
        {
            Scope = repository is null ? "Account" : "Repository",
            AccountGitHubId = account.GitHubId,
            RepositoryGitHubId = repository?.GitHubId,
            Description = request.Description,
            CreatedByUserId = user.Id!,
            CreatedAtUtc = DateTime.UtcNow,
        };
        await session.StoreAsync(token, ApiToken.DocumentId(ApiTokenService.Hash(tokenValue)), cancellationToken);
        await session.SaveChangesAsync(cancellationToken);

        // The plaintext value exists only in this response.
        return Ok(new CreatedToken(tokenValue, account.Login, request.Description, token.Scope, repository?.FullName));
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<TokenInfo>>> List([FromQuery] string account, CancellationToken cancellationToken)
    {
        var resolved = await ResolveAccount(account, cancellationToken);
        if (resolved is null) return Ok(Enumerable.Empty<TokenInfo>());
        if (!await accountAccess.CanManageAnyRepositoryAsync(resolved.GitHubId, cancellationToken))
            return Forbid();

        var tokens = await session.Query<ApiToken>()
            .Where(t => t.AccountGitHubId == resolved.GitHubId)
            .ToListAsync(cancellationToken);

        var repositories = await session.LoadAsync<Repository>(
            tokens.Where(t => t.RepositoryGitHubId is not null)
                  .Select(t => Repository.DocumentId(t.RepositoryGitHubId!.Value))
                  .Distinct(),
            cancellationToken);

        return Ok(tokens
            .OrderByDescending(t => t.CreatedAtUtc)
            .Select(t => new TokenInfo(t.Id!, resolved.Login, t.Description, t.Scope,
                t.RepositoryGitHubId is null ? null
                    : repositories.GetValueOrDefault(Repository.DocumentId(t.RepositoryGitHubId.Value))?.FullName,
                t.CreatedAtUtc, t.RevokedAtUtc)));
    }

    [HttpDelete("{hash}")]
    public async Task<IActionResult> Revoke(string hash, CancellationToken cancellationToken)
    {
        var token = await session.LoadAsync<ApiToken>(ApiToken.DocumentId(hash), cancellationToken);
        if (token is null) return NotFound();

        if (token.AccountGitHubId is null)
            return Forbid();

        // Revoking matches the reach of what is being revoked: a
        // repository-scoped token needs admin on that repository, an
        // account-scoped one needs admin across the account. Otherwise a
        // single-repository maintainer could revoke the org's credentials.
        var mayRevoke = token.RepositoryGitHubId is { } repositoryGitHubId
            ? await CanAdministerRepositoryAsync(repositoryGitHubId, cancellationToken)
            : await accountAccess.CanAdministerWholeAccountAsync(token.AccountGitHubId.Value, cancellationToken);
        if (!mayRevoke)
            return Forbid();

        token.RevokedAtUtc = DateTime.UtcNow;
        await session.SaveChangesAsync(cancellationToken);
        return NoContent();
    }
}
