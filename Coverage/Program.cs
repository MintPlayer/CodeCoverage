using System.Text.RegularExpressions;
using System.Threading.RateLimiting;
using Coverage;
using Coverage.ApiTokens;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using MintPlayer.AspNetCore.SpaServices.Extensions;
using MintPlayer.Spark;
using MintPlayer.Spark.Abstractions.Authentication;
using MintPlayer.Spark.Authorization.Extensions;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.Messaging;
using MintPlayer.Spark.Webhooks.GitHub.DevTunnel.Extensions;
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
builder.Services.AddCoverage();
builder.Services.AddSpark(builder.Configuration, spark =>
{
    spark.UseContext<CoverageSparkContext>();

    // Deliberately DenyAll (no security.json): Spark's generic data endpoints are
    // fully denied. All data access goes through our own /api controllers, which
    // mirror the viewer's GitHub permissions. This also sidesteps the open
    // R4-H1 finding (row-level auth missing on query-execute/stream endpoints).
    spark.AddAuthorization();

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

        if (long.TryParse(builder.Configuration["GitHub:Production:AppId"], out var prodId))
            options.ProductionAppId = prodId;

        if (long.TryParse(builder.Configuration["GitHub:Development:AppId"], out var devId))
            options.DevelopmentAppId = devId;

        var smeeUrl = builder.Configuration["GitHub:SmeeChannelUrl"];
        if (!string.IsNullOrEmpty(smeeUrl))
        {
            options.AddSmeeDevTunnel(smeeUrl);
        }
    });
});

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
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("uploads", context => RateLimitPartition.GetFixedWindowLimiter(
        partitionKey: context.User.FindFirst(ApiTokenAuthenticationHandler.TokenHashClaim)?.Value
            ?? context.Connection.RemoteIpAddress?.ToString()
            ?? "anonymous",
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

var app = builder.Build();

app.UseForwardedHeaders();

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseSpaStaticFilesImproved();

app.UseRouting();
app.UseRateLimiter();
app.UseSpark(o => o.SynchronizeModelsIfRequested<CoverageSparkContext>(args));

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
