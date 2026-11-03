using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;
using Starport.Api.Auth;
using Starport.Api.Delivery;
using Starport.Api.Endpoints;
using Starport.Api.Startup;
using Starport.Api.Storage;
using Starport.Application.Access;
using Starport.Application.Publishing;
using Starport.Infrastructure.Accounts;
using Starport.Infrastructure.Database;

var builder = WebApplication.CreateBuilder(args);
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddDbContext<StarportDbContext>((services, options) =>
    options.UseNpgsql(DatabaseConnection.Require(
        services.GetRequiredService<IConfiguration>(),
        services.GetRequiredService<IHostEnvironment>())));
builder.Services.AddOptions<ForwardedHeadersSettings>()
    .Bind(builder.Configuration.GetSection("ForwardedHeaders"))
    .Validate(settings => settings.IsValid(), "ForwardedHeaders settings are invalid.")
    .ValidateOnStart();
builder.Services.AddOptions<ForwardedHeadersOptions>()
    .Configure<Microsoft.Extensions.Options.IOptions<ForwardedHeadersSettings>>(
        (options, settings) => settings.Value.Apply(options));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("launch", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ =>
            new FixedWindowRateLimiterOptions
            {
                PermitLimit = 30, Window = TimeSpan.FromMinutes(1), QueueLimit = 0
            }));
    options.AddPolicy("upload", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ =>
            new FixedWindowRateLimiterOptions
            {
                PermitLimit = 5, Window = TimeSpan.FromMinutes(1), QueueLimit = 0
            }));
    options.AddPolicy("report", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ =>
            new FixedWindowRateLimiterOptions
            {
                PermitLimit = 5, Window = TimeSpan.FromMinutes(1), QueueLimit = 0
            }));
});
builder.Services.AddHostedService<LaunchCleanupWorker>();
builder.Services.AddSingleton<GameDeliverySettings>();
builder.Services.AddSingleton<DeliveryUsageMeter>();
if (builder.Configuration["Storage:Mode"] == "gcs")
    builder.Services.AddSingleton<GcsGameStorage>();
if (builder.Configuration.GetValue<bool>("Processing:WorkerEnabled"))
{
    if (builder.Configuration["Storage:Mode"] != "gcs" ||
        builder.Configuration["Processing:Mode"] != "job")
        throw new InvalidOperationException("Hosted processing requires GCS and Job mode.");
    builder.Services.AddSingleton<ProcessorJobs>();
    builder.Services.AddHostedService<HostedProcessingWorker>();
}
builder.Services.AddScoped<IGameRepository, GameRepository>();
builder.Services.AddScoped<IPlayGrantRepository, PlayGrantRepository>();
builder.Services.AddScoped<IWorkspaceDeliveryUsage, WorkspaceDeliveryUsageRepository>();
builder.Services.AddScoped<IGameDraftStore, GameDraftStore>();
builder.Services.AddScoped<GameReadService>();
builder.Services.AddScoped<GameCatalogService>();
builder.Services.AddScoped<GameDraftService>();
if (builder.Configuration["Processing:Mode"] == "local" && builder.Environment.IsDevelopment())
{
    builder.Services.AddSingleton<StoragePaths>();
    builder.Services.AddHostedService<LocalProcessingWorker>();
}
if (builder.Configuration["Storage:Mode"] == "gcs" ||
    builder.Configuration["Storage:Mode"] == "local" && builder.Environment.IsDevelopment())
    builder.Services.AddHostedService<ReleaseCleanupWorker>();
if (builder.Configuration.GetValue<bool>("Reconciliation:Enabled"))
    builder.Services.AddHostedService<PublishingReconciliationWorker>();

var workspaceSource = builder.Configuration["Workspaces:Source"];
switch (workspaceSource)
{
    case "local":
        builder.Services.AddScoped<IWorkspaceEntitlements, LocalWorkspaceEntitlements>();
        builder.Services.AddScoped<IWorkspaceMemberships, LocalWorkspaceMemberships>();
        break;
    case "accounts":
        var accounts = builder.Configuration.GetSection(AccountsAccessOptions.Section).Get<AccountsAccessOptions>()
            ?? new AccountsAccessOptions();
        if (!Uri.TryCreate(accounts.BaseUrl, UriKind.Absolute, out var baseUrl) ||
            (baseUrl.Scheme != Uri.UriSchemeHttps &&
             !(baseUrl.Scheme == Uri.UriSchemeHttp &&
               (baseUrl.Host.EndsWith(".svc.cluster.local", StringComparison.Ordinal) ||
                builder.Environment.IsDevelopment() &&
                (baseUrl.Host is "localhost" or "127.0.0.1" or "accounts-api" or "host.docker.internal")))) ||
            !accounts.BaseUrl.EndsWith('/') ||
            !Uri.TryCreate(accounts.TokenUrl, UriKind.Absolute, out var tokenUrl) ||
            (tokenUrl.Scheme != Uri.UriSchemeHttps &&
             !(builder.Environment.IsDevelopment() && tokenUrl.Scheme == Uri.UriSchemeHttp &&
               (tokenUrl.Host is "localhost" or "127.0.0.1" or "keycloak"))) ||
            string.IsNullOrWhiteSpace(accounts.ClientId) || string.IsNullOrWhiteSpace(accounts.ClientSecret))
            throw new InvalidOperationException("Accounts:BaseUrl (ending in /), TokenUrl, ClientId and ClientSecret are required; public URLs must be HTTPS.");
        builder.Services.Configure<AccountsAccessOptions>(builder.Configuration.GetSection(AccountsAccessOptions.Section));
        builder.Services.AddHttpClient("accounts-api", client => client.Timeout = TimeSpan.FromSeconds(5));
        builder.Services.AddHttpClient("accounts-token", client => client.Timeout = TimeSpan.FromSeconds(5));
        builder.Services.AddSingleton<AccountsServiceTokenProvider>();
        builder.Services.AddScoped<AccountsWorkspaceAccess>();
        builder.Services.AddScoped<IWorkspaceEntitlements>(sp => sp.GetRequiredService<AccountsWorkspaceAccess>());
        builder.Services.AddScoped<IWorkspaceMemberships>(sp => sp.GetRequiredService<AccountsWorkspaceAccess>());
        builder.Services.AddHostedService<AccountsServingLossConsumer>();
        break;
    default:
        throw new InvalidOperationException("Workspaces:Source must be 'local' or 'accounts'.");
}
builder.AddStarportAuthentication();

var app = builder.Build();
var migrationMode = builder.Configuration["Database:Migrate"] ?? "skip";
if (migrationMode is "startup" or "only")
{
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<StarportDbContext>().Database.MigrateAsync();
    if (migrationMode == "only")
        return;
}
else if (migrationMode != "skip")
    throw new InvalidOperationException("Database:Migrate must be 'startup', 'only' or 'skip'.");

app.UseForwardedHeaders();
if (!app.Environment.IsDevelopment()) app.UseHsts();
app.UseRouting();
app.UseRateLimiter();
app.Use(async (context, next) =>
{
    var delivery = context.RequestServices.GetRequiredService<GameDeliverySettings>();
    if (delivery.TryGameHost(context.Request.Host.Host, out _) &&
        (context.Request.Path.StartsWithSegments("/api") ||
         context.Request.Path.StartsWithSegments("/health")))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }
    await next();
});
app.UseAuthentication();
app.UseAuthorization();
app.Use(async (context, next) =>
{
    try { await next(); }
    catch (AccountsAccessUnavailableException error)
    {
        app.Logger.LogError(error, "Accounts entitlement check failed");
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        await context.Response.WriteAsJsonAsync(new { code = "account-not-ready", title = "Access checks are unavailable" });
    }
});

app.MapGet("/api/config", () => Results.Ok(new
{
    product = "starport",
    version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0",
    auth = new
    {
        mode = builder.Configuration["Auth:Mode"] == "development" ? "local" : "oidc",
        authority = builder.Configuration["Auth:Oidc:Authority"],
        clientId = builder.Configuration["Auth:Oidc:ClientId"],
        accountUrl = builder.Configuration["Accounts:PublicUrl"]
    },
    features = new
    {
        studio = true,
        publishing = builder.Configuration["Processing:Mode"] is "local" or "job",
        cloudSaves = true
    }
}));
app.MapGet("/health/live", () => Results.Text("healthy"));
app.MapGet("/health/ready", async (StarportDbContext db, CancellationToken ct) =>
    await db.Database.CanConnectAsync(ct) ? Results.Text("ready") : Results.StatusCode(503));

app.MapWorkspaceEndpoints();
app.MapGameEndpoints();
app.MapPublicationEndpoints();
app.MapReleaseActivationEndpoints();
app.MapGameSaveEndpoints();
app.MapGameDeliveryEndpoints();
app.MapPeopleSharingEndpoints();
app.MapGameReviewEndpoints();
app.MapGameModerationEndpoints();
app.MapPlayerDataEndpoints();
app.MapWorkspaceUsageEndpoints();
app.MapUploadEndpoints();

app.Run();

public partial class Program;
