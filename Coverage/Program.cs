using System.Text.RegularExpressions;
using System.Threading.RateLimiting;
using Coverage;
using Coverage.ApiTokens;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using MintPlayer.AspNetCore.SpaServices.Extensions;
using MintPlayer.Spark;
using MintPlayer.Spark.Abstractions.Authentication;
using MintPlayer.Spark.Authorization.Extensions;
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

    spark.AddAuthentication<SparkUser>(configureProviders: identity =>
    {
        // Only register the provider when configured: the OAuth handler validates
        // ClientId on first request and would 500 every request otherwise. Without
        // credentials the app still boots — the sign-in button just won't work.
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

    spark.AddMessaging();
    spark.AddRecipients();
    spark.AddCronJobs();
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

// Spark's built-in rate limiter only covers /spark/* — our ingest endpoints
// need their own, partitioned per token (falling back to client IP).
// The limiter middleware runs BEFORE authentication (UseSpark wires auth
// later in the pipeline), so context.User is always anonymous here — the
// partition key must come from the presented credential itself, not claims.
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
    options.AddPolicy("uploads", context => RateLimitPartition.GetFixedWindowLimiter(
        partitionKey: UploadsPartitionKey(context),
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 60,
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
app.UseRateLimiter();
app.UseSpark();

app.UseEndpoints(endpoints =>
{
    endpoints.MapControllers();
    endpoints.MapSpark();
    endpoints.MapGet("/health", () => Results.Ok());
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
