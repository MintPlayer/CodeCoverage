using Coverage.Entities;
using MintPlayer.Spark;
using Raven.Client.Documents.Linq;

namespace Coverage;

public class CoverageSparkContext : SparkContext
{
    public IRavenQueryable<Account> Accounts => Session.Query<Account>();
    public IRavenQueryable<Repository> Repositories => Session.Query<Repository>();
    public IRavenQueryable<Commit> Commits => Session.Query<Commit>();
    public IRavenQueryable<Build> Builds => Session.Query<Build>();
}
