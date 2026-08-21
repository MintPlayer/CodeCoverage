using Coverage.Entities;
using MintPlayer.Spark;
using Raven.Client.Documents.Linq;

namespace Coverage;

public partial class CoverageSparkContext : SparkContext
{
    public IRavenQueryable<Account> Accounts => Session.Query<Account>();
    public IRavenQueryable<Repository> Repositories => Session.Query<Repository>();
    public IRavenQueryable<Commit> Commits => Session.Query<Commit>();
    public IRavenQueryable<Build> Builds => Session.Query<Build>();

    /// <summary>
    /// Declares <see cref="MyAccountRow"/> as a model root so it gets its own
    /// <c>App_Data/Model</c> file, and therefore its own grid columns — the only
    /// way to keep viewer-dependent columns off the Account grid while Spark
    /// derives columns per entity rather than per query (Spark#284).
    /// </summary>
    /// <remarks>
    /// Rows are computed per request by <c>MyAccountRowActions.My_Accounts</c> and
    /// reached through a <c>Custom.*</c> source, so nothing reads this getter on
    /// the path that matters — model synchronization reflects over property
    /// *types* and never invokes a getter.
    /// <para>
    /// It still queries the (empty) collection rather than throwing, because
    /// declaring a root makes the synchronizer emit a <c>Database.MyAccountRows</c>
    /// query for it. A throwing getter would turn that generated query into a 500;
    /// this way it is simply an empty grid, and the type's read right is scoped to
    /// <c>Authenticated</c> in security.json so it is not an anonymous surface
    /// either.
    /// </para>
    /// </remarks>
    public IRavenQueryable<MyAccountRow> MyAccountRows => Session.Query<MyAccountRow>();
}
