using System.Text.RegularExpressions;
using System.Threading.RateLimiting;
using Coverage;
using Coverage.ApiTokens;
using Coverage.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using MintPlayer.AspNetCore.SpaServices.Extensions;
using MintPlayer.Spark;
using MintPlayer.Spark.Abstractions.Authentication;
using MintPlayer.Spark.Authorization.Configuration;
using MintPlayer.Spark.Authorization.Extensions;
using MintPlayer.Spark.Extensions;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.Messaging;
using MintPlayer.Spark.Webhooks.GitHub.Extensions;

var builder = WebApplication.CreateBuilder(args);

var envPrefix = builder.Environment.EnvironmentName;

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});

builder.Services.AddControllers()
    .AddJsonOptions(options => options.JsonSerializerOptions.Converters.Add(
        new System.Text.Json.Serialization.JsonStringEnumConverter()));
builder.Services.AddHttpContextAccessor();
builder.Services.AddHttpClient();
builder.Services.AddMemoryCache();
// Bounded, and separate from the shared cache on purpose — see SourceContentCache.
builder.Services.AddSingleton<Coverage.Services.ISourceContentCache, Coverage.Services.SourceContentCache>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddCoverage();
builder.Services.AddSpark(builder.Configuration, spark =>
{
    spark.UseContext<CoverageSparkContext>();

    // security.json grants QueryRead on the four entity types to Everyone —
    // including anonymous callers — and the Actions classes (Coverage/Actions)
    // are the only gate behind that: row filters scope reads per viewer (public
    // repos for anonymous, GitHub-granted owners for signed-in users) and redact
    // BadgeToken/InstallationId for non-managers. Writes stay denied at the type
    // level (no Edit/New/Delete right exists), so the generic UI is read-only.
    // The /api controllers remain the primary read surface for the vanity pages.
    spark.AddAuthorization();
    spark.AddActions();

    // GitHub is the only identity this app has, so the entire local-credential
    // surface is switched off: no register, no password login, no forgot/reset,
    // no confirm/resend. Those endpoints were reachable and unused — register is
    // an enumeration oracle, the recovery family is an unauthenticated mail-send
    // trigger, and login distinguishes LockedOut from NotAllowed. This is the
    // server half of the same decision app.routes.ts makes on the client.
    //
    // Disabled REFUSES TO BOOT without an external provider, by design: an
    // authentication surface nobody can sign into is a misconfiguration, not a
    // degraded mode. That is a deliberate change from the previous behaviour
    // noted below — a missing ClientId used to boot with a dead sign-in button,
    // and would now leave local credentials as the only way in, which is exactly
    // what this turns off. Failing fast with Spark's named error beats that.
    spark.AddAuthentication<SparkUser>(
        configure: auth => auth.LocalCredentials = SparkLocalCredentials.Disabled,
        configureProviders: identity =>
    {
        // Only register the provider when configured: the OAuth handler validates
        // ClientId on first request and would 500 every request otherwise.
        var gitHubClientId = builder.Configuration[$"GitHub:{envPrefix}:ClientId"];
        if (!string.IsNullOrEmpty(gitHubClientId))
        {
            identity.AddGitHub(options =>
            {
                options.ClientId = gitHubClientId;
                options.ClientSecret = builder.Configuration[$"GitHub:{envPrefix}:ClientSecret"] ?? string.Empty;
                options.SaveTokens = true;
                // GitHub can hit the callback with a code but no OAuth state —
                // notably the App's "Request user authorization during
                // installation" flow, which our server never initiated. Without
                // this the handler throws and the user gets a 500 instead of
                // the app; the real sign-in path is unaffected (it always has
                // state). Sign-in itself stays available via the shell button.
                options.Events.OnRemoteFailure = context =>
                {
                    context.Response.Redirect("/home");
                    context.HandleResponse();
                    return Task.CompletedTask;
                };
            });
        }
    });
    // Registered as a Spark credential scheme (non-ambient): the composite
    // default-authenticate scheme tries it, which both silences the
    // "refused by every registered scheme" warning on CI uploads and earns
    // the non-ambient antiforgery exemption. The handler returns NoResult
    // for anything that isn't a covt_ value, so this widens nothing.
    // GitHubOidc is deliberately NOT a credential scheme — workflow JWTs
    // stay valid only on endpoints that name the scheme explicitly.
    spark.AddCredentialScheme<AuthenticationSchemeOptions, ApiTokenAuthenticationHandler>(
        ApiTokenAuthenticationHandler.SchemeName);

    // Meters /spark (Spark's generic query API — a second anonymous read surface
    // over the same documents as /api/browse) and /api/browse itself, one bucket
    // per client IP. Registered at the BeforeAuthentication stage since
    // preview.52, so a flood is rejected before a covt_ token lookup is paid for
    // — which is why this replaced a hand-rolled GlobalLimiter rather than
    // sitting alongside one. Assigning PathPrefixes replaces the defaults, hence
    // /connect is listed even though this app has no Identity endpoints: naming
    // it costs nothing and stops the omission becoming a surprise if one is ever
    // added. Named policies below still apply per endpoint on top of this.
    spark.AddRateLimiter(rateLimiter =>
        rateLimiter.PathPrefixes = ["/spark", "/connect", "/api/browse"]);

    spark.AddMessaging();
    spark.AddRecipients();
    spark.AddCronJobs();
    // Pending ISparkMigration classes run inside UseSpark(), after indexes are
    // created and before the app serves — once per database, in Version order,
    // under a cluster-wide lock. Committed and replayed automatically, so a
    // restored backup or a fresh environment can't miss one the way a hand-run
    // patch in Raven Studio can.
    spark.AddMigrations();
    spark.AddGithubWebhooks(options =>
    {
        options.WebhookSecret = builder.Configuration["GitHub:WebhookSecret"] ?? string.Empty;
        options.ClientId = builder.Configuration[$"GitHub:{envPrefix}:ClientId"];
        options.PrivateKeyPath = builder.Configuration[$"GitHub:{envPrefix}:PrivateKeyPath"];

        // ProductionAppId = "the App whose webhooks THIS instance processes"
        // — locally that's the dev App. DevelopmentAppId means something else
        // entirely: "forward that App's webhooks to connected dev clients
        // instead of processing them", a production-side setting. Setting it
        // on a local machine makes the processor silently skip every
        // recipient (Spark webhooks README warns exactly this).
        if (long.TryParse(builder.Configuration[$"GitHub:{envPrefix}:AppId"], out var appId))
            options.ProductionAppId = appId;

        if (!builder.Environment.IsDevelopment()
            && long.TryParse(builder.Configuration["GitHub:Development:AppId"], out var devAppId))
            options.DevelopmentAppId = devAppId;

        // Deliberately NOT options.AddSmeeDevTunnel(smeeUrl): re-minifying the
        // smee-relayed body is necessary (GitHub signs minified bytes), but
        // Spark's tunnel does it via a Newtonsoft round-trip that rewrites
        // fractional-second timestamps — so every installation event fails
        // signature validation. Our lexically-minifying replacement is
        // registered below; upstream fix tracked in docs/spark-handoff.md.
    });
});

// Key ring in RavenDB instead of the container filesystem, where a redeploy
// destroyed it and signed everyone out (auth + antiforgery cookies both
// decrypt with these keys). Configured through options so the IDocumentStore
// that AddSpark registers is resolved lazily, not at registration time.
builder.Services.AddDataProtection().SetApplicationName("Coverage");
builder.Services.AddOptions<Microsoft.AspNetCore.DataProtection.KeyManagement.KeyManagementOptions>()
    .Configure<Raven.Client.Documents.IDocumentStore>((options, store) =>
        options.XmlRepository = new Coverage.Services.RavenDataProtectionKeyRepository(store));

if (!string.IsNullOrEmpty(builder.Configuration["GitHub:SmeeChannelUrl"]))
{
    builder.Services.AddHostedService<Coverage.Services.SmeeWebhookTunnelService>();
}

// GitHubOidc: GitHub-signed workflow JWTs, validated against GitHub's JWKS;
// the audience must be this deployment's public base URL and the action must
// request exactly that audience. (ApiToken is registered inside AddSpark as a
// credential scheme — see above.)
builder.Services.AddAuthentication()
    .AddJwtBearer(GitHubOidc.SchemeName, options =>
    {
        options.Authority = GitHubOidc.Issuer;
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new Microsoft.IdentityModel.Tokens.TokenValidationParameters
        {
            ValidIssuer = GitHubOidc.Issuer,
            ValidAudience = builder.Configuration["Coverage:BaseUrl"] ?? "https://localhost:5200",
            ValidateLifetime = true,
        };
    });

// Ingest endpoints are partitioned per token (falling back to client IP).
// The limiter middleware runs BEFORE authentication, so context.User is always
// anonymous here — the partition key must come from the presented credential
// itself, not claims. That ordering is the framework's since preview.52; before
// it, this app hand-rolled the limiter specifically to keep it.
static string UploadsPartitionKey(HttpContext context)
{
    var authorization = context.Request.Headers.Authorization.ToString();
    if (authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        authorization = authorization[7..];
    else if (authorization.StartsWith("Token ", StringComparison.OrdinalIgnoreCase))
        authorization = authorization[6..];
    if (authorization.StartsWith("covt_", StringComparison.Ordinal))
        return ApiTokenService.Hash(authorization);
    return context.Connection.RemoteIpAddress?.ToString() ?? "anonymous";
}

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    // Browsing is anonymous for public repositories, and GetFile costs a GitHub
    // fetch per uncached path against the installation's shared rate limit — so
    // an unmetered crawler spends a budget every tenant depends on. Roomy enough
    // that the SPA never notices: a page view is a handful of requests.
    options.AddPolicy("browse", context => RateLimitPartition.GetFixedWindowLimiter(
        partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 300,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        }));
    options.AddPolicy("uploads", context => RateLimitPartition.GetFixedWindowLimiter(
        partitionKey: UploadsPartitionKey(context),
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 60,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        }));
    // Polling the status endpoint is metered separately from uploading to it.
    // Same partition — a CI caller gets its own bucket keyed on its token, never
    // collateral damage from a crawler on a shared IP — but a much higher limit,
    // because the two are nothing alike: `uploads` is sized for 50 MB payloads,
    // while a gate waiting on a build spends 12 requests/minute per waiting job
    // and a workflow may wait in several. Sharing one bucket would throttle the
    // poll and starve the uploads it is waiting for.
    options.AddPolicy("uploads-status", context => RateLimitPartition.GetFixedWindowLimiter(
        partitionKey: UploadsPartitionKey(context),
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 300,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        }));
    // Badges get their own, roomier policy: GitHub's camo proxy funnels every
    // README render through a handful of IPs, so sharing the uploads policy
    // would let one popular badge throttle them all.
    options.AddPolicy("badges", context => RateLimitPartition.GetFixedWindowLimiter(
        partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 600,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        }));
});

builder.Services.AddSpaStaticFilesImproved(configuration =>
{
    configuration.RootPath = "ClientApp/dist/ClientApp/browser";
});

// Model synchronization is a build step, not a run mode: it reflects over the entity classes to
// write App_Data/Model/*.json and modelHashes.json, needs no database, and so runs here and
// returns before Build(). --spark-verify-model is the same call in read-only mode (exit 3 on
// drift), which is what lets CI gate the model without a RavenDB.
if (builder.SynchronizeSparkModelsIfRequested(args))
    return;

var app = builder.Build();

app.UseForwardedHeaders();

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseSpaStaticFilesImproved();

app.UseRouting();
// No app.UseRateLimiter() here: spark.AddRateLimiter() registers it through the
// builder registry, at the BeforeAuthentication stage. Calling it here as well
// would put two RateLimitingMiddleware instances in the pipeline — ASP.NET Core
// has no idempotence marker on either — so every request would take two leases
// from the same partition and silently get half its configured budget.
app.UseSpark();

app.UseEndpoints(endpoints =>
{
    endpoints.MapControllers();
    endpoints.MapSpark();
    endpoints.MapGet("/health", () => Results.Ok());
    // Readiness that can actually fail (#13 U1 / roadmap T0.4): 503 only when
    // the GitHub App key is decisively unusable. The compose healthcheck keeps
    // probing /health (a bad key must not restart-loop the container); the
    // deploy workflow polls this and fails the deploy instead.
    endpoints.MapGet("/health/ready", async (IGitHubAppReadinessService readiness, CancellationToken cancellationToken) =>
    {
        var gitHubApp = await readiness.CheckAsync(cancellationToken);
        // Status only, never Detail: this endpoint is anonymous and Detail carries
        // exception text, which on a misconfigured host is the absolute path of
        // the GitHub App private key. The classification is what a probe needs;
        // the diagnosis belongs in the log.
        var payload = new
        {
            status = gitHubApp.Status == GitHubAppReadiness.Failed ? "unready" : "ready",
            gitHubApp = new { gitHubApp.Status },
        };
        return gitHubApp.Status == GitHubAppReadiness.Failed
            ? Results.Json(payload, statusCode: StatusCodes.Status503ServiceUnavailable)
            : Results.Json(payload);
    });
});

app.UseWhen(
    context => !context.Request.Path.StartsWithSegments("/spark")
        && !context.Request.Path.StartsWithSegments("/api")
        && !context.Request.Path.StartsWithSegments("/badge"),
    appBuilder =>
    {
        appBuilder.UseSpaImproved(spa =>
        {
            spa.Options.SourcePath = "ClientApp";

            if (app.Environment.IsDevelopment())
            {
                spa.UseAngularCliServer(npmScript: "start", cliRegexes: [openBrowserRegex()]);
            }
        });
    });

app.Run();

partial class Program
{
    [GeneratedRegex(@"Local\:\s+(?<openbrowser>https?\:\/\/(.+))")]
    private static partial Regex openBrowserRegex();
}
