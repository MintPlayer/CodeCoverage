using Coverage.Services;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Actions;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Actions;

namespace Coverage.CustomActions;

/// <summary>
/// Drops the cached GitHub visibility for the signed-in caller, so the next read re-queries
/// GitHub for their organizations and App installations. The manual counterpart of the 5-minute
/// TTL, and the same work <c>POST /api/me/accounts/resync</c> does.
/// </summary>
/// <remarks>
/// Operates on the caller, not on rows: <c>selectionRule "=0"</c> in <c>customActions.json</c>,
/// and neither <c>Parent</c> nor <c>SelectedItems</c> is read. It is offered on
/// <c>MyAccountRow</c> because that is the only type <c>Resync/{Type}</c> is granted on — actions
/// attach by right, not by declaration.
/// <para>
/// The refresh is a client operation rather than a return value: invalidating the cache changes
/// nothing the caller is currently looking at until the grid re-runs its query, and the grid is
/// the thing that has to be told.
/// </para>
/// </remarks>
public partial class ResyncAction : SparkCustomAction
{
    [Inject] private readonly IGitHubAccessService gitHubAccess;
    [Inject] private readonly IManager manager;

    public override async Task ExecuteAsync(CustomActionArgs args, CancellationToken cancellationToken = default)
    {
        await gitHubAccess.InvalidateAsync(cancellationToken);
        manager.Client.RefreshQuery("my-accounts");
    }
}
