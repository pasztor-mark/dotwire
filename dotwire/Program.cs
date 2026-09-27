using Dotwire.Api;
using Dotwire.Auth;
using Dotwire.Configuration;
using Dotwire.Crypto;
using Dotwire.Data;
using Dotwire.Nats;
using Dotwire.Realtime;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using NATS.Client.Core;
using NATS.Client.JetStream;
using Npgsql;
using System.Security.Claims;
using System.Threading.RateLimiting;

DotNetEnv.Env.TraversePath().Load();

var builder = WebApplication.CreateSlimBuilder(args);

builder.Services.Configure<DotwireOptions>(builder.Configuration.GetSection(DotwireOptions.SectionName));
builder.Services.Configure<NatsOptions>(builder.Configuration.GetSection(NatsOptions.SectionName));
builder.Services.Configure<PostgresOptions>(builder.Configuration.GetSection(PostgresOptions.SectionName));
builder.Services.Configure<EncryptionOptions>(builder.Configuration.GetSection(EncryptionOptions.SectionName));
builder.Services.Configure<WebhooksOptions>(builder.Configuration.GetSection(WebhooksOptions.SectionName));
builder.Services.Configure<SseOptions>(builder.Configuration.GetSection(SseOptions.SectionName));
builder.Services.Configure<RateLimitOptions>(builder.Configuration.GetSection(RateLimitOptions.SectionName));
builder.Services.Configure<RetentionOptions>(builder.Configuration.GetSection(RetentionOptions.SectionName));
builder.Services.AddSingleton<MessageCipher>();

var authOptions = builder.Configuration.GetSection(AuthOptions.SectionName).Get<AuthOptions>() ?? new AuthOptions();
var keyRing = new SigningKeyRing(
    authOptions,
    logger: LoggerFactory.Create(b => b.AddConsole()).CreateLogger(nameof(SigningKeyRing)));

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = authOptions.Issuer,
            ValidAudience = authOptions.Audience,
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            IssuerSigningKeyResolver = (_, _, kid, _) => keyRing.Resolve(kid),
            NameClaimType = "sub",
        };
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];
                var path = context.HttpContext.Request.Path;
                if (!string.IsNullOrEmpty(accessToken) &&
                    (path.StartsWithSegments("/hub/rooms") || path.Value?.EndsWith("/events") == true))
                {
                    context.Token = accessToken;
                }
                return Task.CompletedTask;
            },
            OnTokenValidated = context =>
            {
                var principal = context.Principal;
                if (string.IsNullOrEmpty(principal?.FindFirstValue("sub")))
                    context.Fail("Token has no sub claim.");
                else if (principal.FindFirstValue("dw:role")
                         is not ("member" or "auditor" or "admin"))
                    context.Fail("Token has no valid dw:role claim.");
                return Task.CompletedTask;
            },
        };
    });
builder.Services.AddAuthorization();
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials()
            .SetIsOriginAllowed(_ => true);
    });
});

builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, DotwireJsonContext.Default));

builder.Services.AddSingleton<HubRateLimitFilter>();
builder.Services.AddSignalR(options =>
{
    options.EnableDetailedErrors = false;
    options.KeepAliveInterval = TimeSpan.FromSeconds(30);
    options.AddFilter<HubRateLimitFilter>();
}).AddJsonProtocol(options =>
{
    options.PayloadSerializerOptions.TypeInfoResolverChain.Insert(0, DotwireJsonContext.Default);
});

// Token-bucket rate limits (spec §3.10). Partitioned by the `sub` claim; idle partitions are
// evicted by the runtime. Disabled buckets (Dotwire:RateLimits:Enabled=false) never reject.
builder.Services.AddRateLimiter(options =>
{
    options.OnRejected = async (context, ct) =>
    {
        var retryAfterSeconds = 1;
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
            retryAfterSeconds = (int)Math.Ceiling(retryAfter.TotalSeconds);
        await ApiResults.RateLimited(retryAfterSeconds).ExecuteAsync(context.HttpContext);
    };

    AddUserBucketPolicy(options, "send", o => o.Send);
    AddUserBucketPolicy(options, "read", o => o.Read);
    AddUserBucketPolicy(options, "admin", o => o.Admin);
});

builder.Services.AddKeyedSingleton<NpgsqlDataSource>(PostgresDataSources.Read,
    (sp, _) => NpgsqlDataSource.Create(RequireConnectionString(sp.GetRequiredService<IConfiguration>(), "PostgresRead")));
builder.Services.AddKeyedSingleton<NpgsqlDataSource>(PostgresDataSources.Write,
    (sp, _) => NpgsqlDataSource.Create(RequireConnectionString(sp.GetRequiredService<IConfiguration>(), "PostgresWrite")));

builder.Services.AddSingleton<INatsConnection>(sp =>
{
    var options = sp.GetRequiredService<IOptions<NatsOptions>>().Value;
    return new NatsConnection(new NatsOpts { Url = options.Url });
});
builder.Services.AddSingleton<INatsJSContext>(sp =>
    new NatsJSContext(sp.GetRequiredService<INatsConnection>()));

builder.Services.AddSingleton<RoomInterestManager>();
builder.Services.AddSingleton<PresenceCoalescer>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<PresenceCoalescer>());
builder.Services.AddSingleton<AuditPublisher>();

// One pooled HttpClient for both presend and postsend webhooks (spec §3.8), AOT-safe.
builder.Services.AddHttpClient(PresendWebhookClient.HttpClientName)
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
    });
builder.Services.AddSingleton<PresendWebhookClient>();
builder.Services.AddSingleton<PostSendWebhookService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<PostSendWebhookService>());

var nodeRole = builder.Configuration.GetSection(DotwireOptions.SectionName).Get<DotwireOptions>()?.Role ?? NodeRole.All;
if (nodeRole is NodeRole.All or NodeRole.Api)
{
    builder.Services.AddHostedService<PostgresWriterService>();
    builder.Services.AddHostedService<AuditWriterService>();
}

var app = builder.Build();

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

if (nodeRole is NodeRole.All or NodeRole.Api)
{
    Messages.MapMessageEndpoints(app);
    Participants.MapParticipantEndpoints(app);
    Admin.MapAdminEndpoints(app);
    Audit.MapAuditEndpoints(app);
}

if (nodeRole is NodeRole.All or NodeRole.Gateway)
{
    app.MapHub<RoomHub>("/hub/rooms");
    Events.MapEventEndpoints(app);
}

app.MapGet("/healthz", () => Results.Ok());

var postgres = app.Services.GetRequiredService<IOptions<PostgresOptions>>().Value;
if (postgres.Migrate)
{
    await MigrationRunner.RunAsync(
        RequireConnectionString(app.Configuration, "PostgresMigrator"),
        postgres.AppRolePassword,
        app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(MigrationRunner)));

    // Retention (spec §3.11): unset means leave whatever the API last set via PUT /admin/retention.
    var retentionDays = app.Services.GetRequiredService<IOptions<RetentionOptions>>().Value.MessagesDays;
    if (retentionDays.HasValue)
    {
        await using var conn = new NpgsqlConnection(RequireConnectionString(app.Configuration, "PostgresMigrator"));
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT set_message_retention($1)", conn);
        cmd.Parameters.AddWithValue(retentionDays.Value);
        await cmd.ExecuteScalarAsync();
    }
}

var nats = app.Services.GetRequiredService<IOptions<NatsOptions>>().Value;
if (nats.Enabled)
{
    await JetStreamProvisioner.ProvisionAsync(
        app.Services.GetRequiredService<INatsJSContext>(),
        nats,
        app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(JetStreamProvisioner)));
}

app.Run();

static string RequireConnectionString(IConfiguration configuration, string name) =>
    configuration.GetConnectionString(name)
    ?? throw new InvalidOperationException($"ConnectionStrings:{name} is not configured.");

// Per-user (sub claim) token-bucket policy. Disabled globally via Dotwire:RateLimits:Enabled=false.
static void AddUserBucketPolicy(RateLimiterOptions options, string name, Func<RateLimitOptions, RateLimitBucketOptions> selector)
{
    options.AddPolicy(name, httpContext =>
    {
        var rateLimits = httpContext.RequestServices.GetRequiredService<IOptions<RateLimitOptions>>().Value;
        if (!rateLimits.Enabled)
            return RateLimitPartition.GetNoLimiter("disabled");

        var bucket = selector(rateLimits);
        var key = httpContext.User.FindFirstValue("sub") ?? "anonymous";
        return RateLimitPartition.GetTokenBucketLimiter(key, _ => new TokenBucketRateLimiterOptions
        {
            TokenLimit = bucket.PermitLimit,
            TokensPerPeriod = bucket.TokensPerPeriod,
            ReplenishmentPeriod = TimeSpan.FromSeconds(bucket.PeriodSeconds),
            AutoReplenishment = true,
            QueueLimit = 0,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
        });
    });
}

public partial class Program;
