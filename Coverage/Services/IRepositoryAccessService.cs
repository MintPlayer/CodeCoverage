using Coverage.Entities;

namespace Coverage.Services;

/// <summary>
/// "What may this viewer do with this repository." The single seam every read
/// and management gate goes through, replacing a cached <c>IsPrivate</c> boolean
/// plus an owner-granular login list — neither of which ever asked GitHub about
/// the repository actually being requested.
/// </summary>
public interface IRepositoryAccessService
{
    /// <summary>
    /// The viewer's level for this repository, refreshing the public/private
    /// lease first if it has expired. Public repositories are
    /// <see cref="RepositoryAccessLevel.Read"/> for everyone, including anonymous
    /// callers — that is a product decision, not an oversight.
    /// <para>
    /// May mutate and persist <paramref name="repository"/>'s
    /// <c>IsPrivate</c>/<c>VisibilityCheckedAtUtc</c> as a side effect of the
    /// refresh. That is deliberate: the lease is only useful if reading it also
    /// renews it.
    /// </para>
    /// </summary>
    Task<RepositoryAccessLevel> GetAsync(Repository repository, CancellationToken cancellationToken = default);

    /// <summary>
    /// As <see cref="GetAsync"/>, but for a private repository the answer is
    /// re-derived from GitHub rather than from the snapshot.
    /// <para>
    /// For the one path where an hour-old snapshot is not good enough: serving
    /// private **source code**, which is both the sharpest disclosure in this
    /// system and the place the app lends out its installation token. Viewing
    /// private source is low-frequency, so the extra call is not the
    /// per-request shape the bulk design exists to avoid.
    /// </para>
    /// </summary>
    Task<RepositoryAccessLevel> GetVerifiedAsync(Repository repository, CancellationToken cancellationToken = default);
}
