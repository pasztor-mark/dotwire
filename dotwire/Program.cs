using Dotwire.Api;
using Dotwire.Auth;
using Dotwire.Configuration;
using Dotwire.Crypto;
using Dotwire.Data;
using Dotwire.Nats;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using NATS.Client.Core;
using NATS.Client.JetStream;
using Npgsql;
using System.Security.Claims;

DotNetEnv.Env.TraversePath().Load();

var builder = WebApplication.CreateSlimBuilder(args);

builder.Services.Configure<DotwireOptions>(builder.Configuration.GetSection(DotwireOptions.SectionName));
builder.Services.Configure<NatsOptions>(builder.Configuration.GetSection(NatsOptions.SectionName));
builder.Services.Configure<PostgresOptions>(builder.Configuration.GetSection(PostgresOptions.SectionName));
builder.Services.Configure<EncryptionOptions>(builder.Configuration.GetSection(EncryptionOptions.SectionName));
builder.Services.AddSingleton<MessageCipher>();

var authOptions = builder.Configuration.GetSection(AuthOptions.SectionName).Get<AuthOptions>() ?? new AuthOptions();
var keyRing = new SigningKeyRing(
    authOptions,
    logger: LoggerFactory.Create(b => b.AddConsole()).CreateLogger(nameof(SigningKeyRing)));

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false; // keep "sub"/"dw:role" as-is, no legacy remapping
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = authOptions.Issuer,
            ValidAudience = authOptions.Audience,
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256], // RS256 only - never symmetric
            IssuerSigningKeyResolver = (_, _, kid, _) => keyRing.Resolve(kid),
            NameClaimType = "sub",
        };
        options.Events = new JwtBearerEvents
        {
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

builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, DotwireJsonContext.Default));

builder.Services.AddKeyedSingleton<NpgsqlDataSource>(PostgresDataSources.Read,
    (_, _) => NpgsqlDataSource.Create(RequireConnectionString(builder.Configuration, "PostgresRead")));
builder.Services.AddKeyedSingleton<NpgsqlDataSource>(PostgresDataSources.Write,
    (_, _) => NpgsqlDataSource.Create(RequireConnectionString(builder.Configuration, "PostgresWrite")));

builder.Services.AddSingleton<INatsConnection>(sp =>
{
    var options = sp.GetRequiredService<IOptions<NatsOptions>>().Value;
    return new NatsConnection(new NatsOpts { Url = options.Url });
});
builder.Services.AddSingleton<INatsJSContext>(sp =>
    new NatsJSContext(sp.GetRequiredService<INatsConnection>()));

var natsEnabled = builder.Configuration.GetSection(NatsOptions.SectionName).Get<NatsOptions>()?.Enabled ?? true;
var nodeRole = builder.Configuration.GetSection(DotwireOptions.SectionName).Get<DotwireOptions>()?.Role ?? NodeRole.All;
if (natsEnabled && nodeRole is NodeRole.All or NodeRole.Api)
    builder.Services.AddHostedService<PostgresWriterService>();

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

Messages.MapMessageEndpoints(app);
app.MapGet("/healthz", () => Results.Ok());

var postgres = app.Services.GetRequiredService<IOptions<PostgresOptions>>().Value;
if (postgres.Migrate)
{
    await MigrationRunner.RunAsync(
        RequireConnectionString(builder.Configuration, "PostgresMigrator"),
        postgres.AppRolePassword,
        app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(MigrationRunner)));
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

public partial class Program;
