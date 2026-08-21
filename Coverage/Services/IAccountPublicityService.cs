using Coverage.Entities;

namespace Coverage.Services;

/// <summary>
/// Keeps <c>Account.PublicRepoCount</c> honest. It exists because the Account row
/// filter must be a single translatable expression and therefore cannot join to
/// Repositories — so the answer has to already be on the account.
///
/// <para>
/// Writers adjust by a **delta** rather than recounting. Recounting looks safer
/// and is not: the count would come from a RavenDB index that has not yet seen
/// the very change being made, so every writer would persist a stale number.
/// A delta is computed from the before/after the writer already holds.
/// </para>
/// <para>
/// <see cref="ReconcileAllAsync"/> is the backstop, and it runs only where no
/// changes are pending so the index it reads is current. Direction matters: too
/// low merely hides an account from anonymous callers, too high exposes one, so
/// the reconciliation is what stops a missed decrement from leaking forever.
/// </para>
/// </summary>
public interface IAccountPublicityService
{
    /// <summary>
    /// Applies a known change in public-repository count. In-memory on a loaded
    /// account; the caller's <c>SaveChanges</c> persists it. Clamped at zero — a
    /// negative count would make the row filter nonsense rather than merely wrong.
    /// </summary>
    void Adjust(Account account, int delta);

    /// <summary>Delta for one repository's visibility moving from <paramref name="wasPrivate"/> to <paramref name="isPrivate"/>.</summary>
    static int DeltaFor(bool wasPrivate, bool isPrivate)
        => (isPrivate ? 0 : 1) - (wasPrivate ? 0 : 1);

    /// <summary>
    /// Recomputes every account's count from the index. The self-healing
    /// backstop — call it only when the session has no pending changes, or it
    /// will read around them.
    /// </summary>
    Task ReconcileAllAsync(CancellationToken cancellationToken = default);
}
