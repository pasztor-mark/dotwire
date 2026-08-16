using System.Security.Cryptography;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace dotwire.Tests;

public class RealtimeHubAuthTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public RealtimeHubAuthTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Postgres:Migrate", "false");
            b.UseSetting("Nats:Enabled", "false");
            b.UseSetting("Auth:Issuer", TestTokens.Issuer);
            b.UseSetting("Auth:Audience", TestTokens.Audience);
            b.UseSetting($"Auth:Keys:{TestKeys.Kid}", TestKeys.PublicPem);
        });
    }

    private HubConnection BuildConnection(string? token = null)
    {
        return new HubConnectionBuilder()
            .WithUrl(new Uri(_factory.Server.BaseAddress, "/hub/rooms"), options =>
            {
                options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                options.WebSocketFactory = async (context, ct) =>
                {
                    var wsClient = _factory.Server.CreateWebSocketClient();
                    return await wsClient.ConnectAsync(context.Uri, ct);
                };
                if (token is not null)
                {
                    options.AccessTokenProvider = () => Task.FromResult<string?>(token);
                }
            })
            .Build();
    }

    [Fact]
    public async Task NoToken_ConnectionFails()
    {
        await using var connection = BuildConnection();
        await Assert.ThrowsAnyAsync<Exception>(() => connection.StartAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ExpiredToken_ConnectionFails()
    {
        var token = TestTokens.Mint("user-1", ttl: TimeSpan.FromMinutes(-30));
        await using var connection = BuildConnection(token);
        await Assert.ThrowsAnyAsync<Exception>(() => connection.StartAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task InvalidRole_ConnectionFails()
    {
        var token = TestTokens.Mint("user-1", role: "superadmin");
        await using var connection = BuildConnection(token);
        await Assert.ThrowsAnyAsync<Exception>(() => connection.StartAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UnknownKid_ConnectionFails()
    {
        var token = TestTokens.Mint("user-1", rsa: RSA.Create(2048), kid: "unknown-kid");
        await using var connection = BuildConnection(token);
        await Assert.ThrowsAnyAsync<Exception>(() => connection.StartAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ValidToken_ConnectsSuccessfully()
    {
        var token = TestTokens.Mint("user-1");
        await using var connection = BuildConnection(token);
        await connection.StartAsync(TestContext.Current.CancellationToken);
        Assert.Equal(HubConnectionState.Connected, connection.State);
        await connection.StopAsync(TestContext.Current.CancellationToken);
    }
}
