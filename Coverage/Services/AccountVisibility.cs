using System.Linq.Expressions;
using Coverage.Entities;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;

namespace Coverage.Services;

/// <summary>
/// The single definition of "which accounts may this viewer see", alongside
/// <see cref="RepositoryVisibility"/> and for the same reason: the rule has to be
/// expressible as one RavenDB predicate, it is the only gate on a type whose
/// read right is granted to Everyone, and a rule that lives inside a hook cannot
/// be tested without standing up the whole Spark pipeline.
///
/// Logins and avatars are public GitHub data, but the *set of accounts present
/// here* is not: an account that exists only because it owns private
/// repositories discloses that this organization uses the service.
/// </summary>
public static class AccountVisibility
{
    /// <summary>
    /// Visible if the account owns something anonymously visible, or if the viewer
    /// is entitled to the account itself.
    /// <para>
    /// <c>PublicRepoCount</c> is denormalized onto the account precisely so this
    /// can be one expression — a row filter cannot join to Repositories. See
    /// <see cref="IAccountPublicityService"/> for how it is kept honest.
    /// </para>
    /// </summary>
    public static Expression<Func<Account, bool>> Filter(long[] allowedOwnerIds)
        => account => account.PublicRepoCount > 0 || account.GitHubId.In(allowedOwnerIds);
}
