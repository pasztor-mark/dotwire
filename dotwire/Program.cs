using Dotwire;
using Dotwire.Api;
using Dotwire.Auth;
using Dotwire.Configuration;
using Dotwire.Crypto;
using Dotwire.Data;
using Dotwire.Nats;
using Dotwire.Realtime;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using NATS.Client.Core;
using NATS.Client.JetStream;
using Npgsql;
using System.Security.Claims;
using System.Threading.RateLimiting;

if (args is ["--healthcheck", ..])
    return await HealthProbe.RunAsync(new HttpClient { Timeout = TimeSpan.FromSeconds(3) }, HealthProbe.DefaultUri());

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

var dotwireOptions = builder.Configuration.GetSection(DotwireOptions.SectionName).Get<DotwireOptions>() ?? new DotwireOptions();
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = dotwireOptions.MaxRequestBodyBytes);

var authOptions = builder.Configuration.GetSection(AuthOptions.SectionName).Get<AuthOptions>() ?? new AuthOptions();
var keyRing = new SigningKeyRing(
    authOptions,
    logger: LoggerFactory.Create(b => b.AddConsole()).CreateLogger(nameof(SigningKeyRing)));

var webhooksOptions = builder.Configuration.GetSection(WebhooksOptions.SectionName).Get<WebhooksOptions>() ?? new WebhooksOptions();
WebhookUrlRules.Validate(webhooksOptions);

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
            ClockSkew = TimeSpan.FromSeconds(authOptions.ClockSkewSeconds),
        };
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var request = context.HttpContext.Request;
                var accessToken = request.Query["access_token"];
                if (!string.IsNullOrEmpty(accessToken) &&
                    string.IsNullOrEmpty(request.Headers.Authorization) &&
                    TokenRules.AcceptsQueryToken(request.Method, request.Path))
                {
                    context.Token = accessToken;
                }
                return Task.CompletedTask;
            },
            OnTokenValidated = context =>
            {
                var failure = TokenRules.Validate(context.Principal, authOptions, DateTimeOffset.UtcNow);
                if (failure is not null)
                    context.Fail(failure);
                return Task.CompletedTask;
            },
        };
    });
builder.Services.AddAuthorization();

var corsSettings = builder.Configuration.GetSection(CorsSettings.SectionName).Get<CorsSettings>() ?? new CorsSettings();
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyHeader().AllowAnyMethod();
        if (corsSettings.AllowedOrigins.Length > 0)
            policy.WithOrigins(corsSettings.AllowedOrigins);
        else
            policy.AllowAnyOrigin();
    });
});

builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, DotwireJsonContext.Default));

builder.Services.AddSingleton<HubRateLimitFilter>();
builder.Services.AddSignalR(options =>
{
    options.EnableDetailedErrors = false;
    options.KeepAliveInterval = TimeSpan.FromSeconds(30);
    options.ClientTimeoutInterval = TimeSpan.FromSeconds(60);
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

    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
    {
        var rateLimits = httpContext.RequestServices.GetRequiredService<IOptions<RateLimitOptions>>().Value;
        if (!rateLimits.Enabled || httpContext.User.Identity?.IsAuthenticated == true)
            return RateLimitPartition.GetNoLimiter("authenticated");

        var bucket = rateLimits.Unauthenticated;
        return RateLimitPartition.GetTokenBucketLimiter($"ip:{httpContext.Connection.RemoteIpAddress}", _ => BucketOptions(bucket));
    });

    AddUserBucketPolicy(options, "send", o => o.Send);
    AddUserBucketPolicy(options, "read", o => o.Read);
    AddUserBucketPolicy(options, "admin", o => o.Admin);
});

builder.Services.AddKeyedSingleton<NpgsqlDataSource>(PostgresDataSources.Read,
    (sp, _) => NpgsqlDataSource.Create(RequireConnectionString(sp.GetRequiredService<IConfiguration>(), "PostgresRead")));
builder.Services.AddKeyedSingleton<NpgsqlDataSource>(PostgresDataSources.Write,
    (sp, _) => NpgsqlDataSource.Create(RequireConnectionString(sp.GetRequiredService<IConfiguration>(), "PostgresWrite")));
builder.Services.AddSingleton<IAccessStore, PostgresAccessStore>();

builder.Services.AddSingleton<INatsConnection>(sp =>
{
    var options = sp.GetRequiredService<IOptions<NatsOptions>>().Value;
    return new NatsConnection(new NatsOpts { Url = options.Url });
});
builder.Services.AddSingleton<INatsJSContext>(sp =>
    new NatsJSContext(sp.GetRequiredService<INatsConnection>()));

builder.Services.AddSingleton<RoomHubLifetimeManager>();
builder.Services.AddSingleton<HubLifetimeManager<RoomHub>>(sp => sp.GetRequiredService<RoomHubLifetimeManager>());
builder.Services.AddSingleton<RoomInterestManager>();
builder.Services.AddSingleton<PresenceCoalescer>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<PresenceCoalescer>());
builder.Services.AddSingleton<TypingThrottle>();
builder.Services.AddSingleton<UserConnectionLimiter>();
builder.Services.AddSingleton<AuditPublisher>();

builder.Services.AddHttpClient(PresendWebhookClient.HttpClientName, client =>
        client.MaxResponseContentBufferSize = PresendWebhookClient.MaxResponseBytes)
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
    });
builder.Services.AddSingleton<PresendWebhookClient>();
builder.Services.AddSingleton<PostSendWebhookService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<PostSendWebhookService>());

var nodeRole = dotwireOptions.Role;
if (nodeRole is NodeRole.All or NodeRole.Api)
{
    builder.Services.AddHostedService<PostgresWriterService>();
    builder.Services.AddHostedService<AuditWriterService>();
}
if (nodeRole is NodeRole.All or NodeRole.Gateway)
    builder.Services.AddHostedService<MembershipSweeper>();

var app = builder.Build();

if (!string.IsNullOrEmpty(webhooksOptions.Presend.Url) || !string.IsNullOrEmpty(webhooksOptions.PostSend.Url))
{
    if (string.IsNullOrEmpty(webhooksOptions.Secret))
        app.Logger.LogWarning("Webhook URLs are configured without Dotwire:Webhooks:Secret; hosts cannot verify webhook calls.");
}

app.UseCors();
app.Use((context, next) =>
{
    if (context.Request.ContentLength > dotwireOptions.MaxRequestBodyBytes)
    {
        context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
        return Task.CompletedTask;
    }
    return next(context);
});
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();

if (nodeRole is NodeRole.All or NodeRole.Api)
{
    Messages.MapMessageEndpoints(app);
    Participants.MapParticipantEndpoints(app);
    Admin.MapAdminEndpoints(app);
    Audit.MapAuditEndpoints(app);
}

if (nodeRole is NodeRole.All or NodeRole.Gateway)
{
    app.MapHub<RoomHub>("/hub/rooms", options =>
    {
        options.Transports = HttpTransportType.WebSockets;
        options.CloseOnAuthenticationExpiration = true;
    }).RequireRateLimiting("read");
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

await keyRing.RefreshAsync();

app.Run();
return 0;

static string RequireConnectionString(IConfiguration configuration, string name) =>
    configuration.GetConnectionString(name)
    ?? throw new InvalidOperationException($"ConnectionStrings:{name} is not configured.");

static TokenBucketRateLimiterOptions BucketOptions(RateLimitBucketOptions bucket) => new()
{
    TokenLimit = bucket.PermitLimit,
    TokensPerPeriod = bucket.TokensPerPeriod,
    ReplenishmentPeriod = TimeSpan.FromSeconds(bucket.PeriodSeconds),
    AutoReplenishment = true,
    QueueLimit = 0,
    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
};

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
        return RateLimitPartition.GetTokenBucketLimiter(key, _ => BucketOptions(bucket));
    });
}

public partial class Program;
