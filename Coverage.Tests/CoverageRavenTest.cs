using Raven.TestDriver;

namespace Coverage.Tests;

/// <summary>
/// Base class for every test that needs an embedded RavenDB.
///
/// <see cref="RavenTestDriver.ConfigureServer"/> configures a **process-wide**
/// server and throws once any store has been created — so it must run exactly
/// once per test assembly, before the first <c>GetDocumentStore</c>. Each test
/// class calling it in its own static constructor happens to work only while
/// the class that runs first is also the one that configures; xUnit runs
/// classes in parallel, so which one that is varies by machine. It passed
/// locally and failed in CI on the same commit.
///
/// Hoisting it here makes the ordering irrelevant: the base type initializer
/// runs before any derived instance exists, and there is only one of it.
/// Derive from this rather than from <see cref="RavenTestDriver"/> directly —
/// then a new test class cannot re-arm the problem by forgetting, or by
/// remembering.
/// </summary>
public abstract class CoverageRavenTest : RavenTestDriver
{
    static CoverageRavenTest()
    {
        ConfigureServer(new TestServerOptions
        {
            Licensing = new Raven.Embedded.ServerOptions.LicensingOptions
            {
                // CI has no RAVENDB_LICENSE; the embedded server runs in its
                // restricted mode rather than refusing to start.
                ThrowOnInvalidOrMissingLicense = false,
            },
        });
    }
}
