using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using Dotwire.Api;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Npgsql;
using Xunit;

namespace dotwire.Tests;

[Collection("Compose")]
public class RealtimeLoadTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly string? _pgPassword;
    private readonly bool _canRunIntegration;

    public RealtimeLoadTests(WebApplicationFactory<Program> factory)
    {
        DotNetEnv.Env.TraversePath().Load();
        _pgPassword = Environment.GetEnvironmentVariable("POSTGRES_PASSWORD");
        var appPassword = Environment.GetEnvironmentVariable("POSTGRES_APP_PASSWORD");

        _canRunIntegration = !string.IsNullOrEmpty(_pgPassword) &&
                             !string.IsNullOrEmpty(appPassword) &&
                             IsReachable("localhost", 5432) &&
                             IsReachable("localhost", 4222);

        _factory = factory.WithWebHostBuilder(b =>
        {
            if (_canRunIntegration)
            {
                b.UseSetting("ConnectionStrings:PostgresMigrator",
                    $"Host=localhost;Port=5432;Database=dotwire;Username=dotwire;Password={_pgPassword}");
                b.UseSetting("ConnectionStrings:PostgresWrite",
                    $"Host=localhost;Port=5432;Database=dotwire;Username=dotwire_app;Password={appPassword}");
                b.UseSetting("ConnectionStrings:PostgresRead",
                    $"Host=localhost;Port=5432;Database=dotwire;Username=dotwire_app;Password={appPassword}");
                b.UseSetting("Postgres:Migrate", "true");
                b.UseSetting("Nats:Url", "nats://localhost:4222");
                b.UseSetting("Nats:Enabled", "true");
            }
            else
            {
                b.UseSetting("Postgres:Migrate", "false");
                b.UseSetting("Nats:Enabled", "false");
            }

            b.UseSetting("Auth:Issuer", TestTokens.Issuer);
            b.UseSetting("Auth:Audience", TestTokens.Audience);
            b.UseSetting($"Auth:Keys:{TestKeys.Kid}", TestKeys.PublicPem);
            b.UseSetting("Dotwire:Encryption:ActiveKeyId", "it");
            b.UseSetting("Dotwire:Encryption:Keys:it",
                Convert.ToBase64String(new byte[32] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32 }));
        });
    }

    private static bool IsReachable(string host, int port)
    {
        try
        {
            using var client = new TcpClient();
            var result = client.BeginConnect(host, port, null, null);
            var success = result.AsyncWaitHandle.WaitOne(TimeSpan.FromMilliseconds(200));
            if (!success) return false;
            client.EndConnect(result);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private HubConnection BuildHubConnection(string token)
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
                options.AccessTokenProvider = () => Task.FromResult<string?>(token);
            })
            .Build();
    }

    private async Task SeedUsersAsync(Guid roomId, IEnumerable<string> users)
    {
        if (!_canRunIntegration) return;
        var connStr = $"Host=localhost;Port=5432;Database=dotwire;Username=dotwire;Password={_pgPassword}";
        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync(TestContext.Current.CancellationToken);

        foreach (var user in users)
        {
            await using var cmdRole = new NpgsqlCommand(
                "INSERT INTO user_roles (user_id, role) VALUES ($1, 'member') ON CONFLICT (user_id) DO NOTHING", conn);
            cmdRole.Parameters.AddWithValue(user);
            await cmdRole.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);

            await using var cmdMember = new NpgsqlCommand(
                "INSERT INTO room_members (room_id, user_id) VALUES ($1, $2) ON CONFLICT DO NOTHING", conn);
            cmdMember.Parameters.AddWithValue(roomId);
            cmdMember.Parameters.AddWithValue(user);
            await cmdMember.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task RealtimeFanout_HighThroughputLoadTest()
    {
        if (!_canRunIntegration)
            Assert.Skip("Postgres / NATS integration environment not running");

        var roomId = Guid.NewGuid();
        const int clientCount = 20;
        const int messagesToSend = 50;
        var expectedDeliveries = clientCount * messagesToSend;

        var userIds = Enumerable.Range(1, clientCount).Select(i => $"load-user-{i}-{Guid.NewGuid():N}").ToList();
        await SeedUsersAsync(roomId, userIds);

        var receivedDeliveries = new ConcurrentBag<(string ClientId, RoomMessageDelivery Message, long ReceivedTicks)>();
        var connections = new List<HubConnection>();

        try
        {
            for (var i = 0; i < clientCount; i++)
            {
                var userId = userIds[i];
                var token = TestTokens.Mint(userId);
                var conn = BuildHubConnection(token);
                var localUserId = userId;

                conn.On<RoomMessageDelivery>("ReceiveMessage", msg =>
                {
                    receivedDeliveries.Add((localUserId, msg, Stopwatch.GetTimestamp()));
                });

                await conn.StartAsync(TestContext.Current.CancellationToken);
                await conn.InvokeAsync("Subscribe", roomId, TestContext.Current.CancellationToken);
                connections.Add(conn);
            }

            await Task.Delay(500, TestContext.Current.CancellationToken);

            var restClient = _factory.CreateClient();
            var publisherToken = TestTokens.Mint(userIds[0]);
            restClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", publisherToken);

            var sw = Stopwatch.StartNew();

            var sendTasks = Enumerable.Range(1, messagesToSend).Select(async i =>
            {
                var content = $"load-payload-burst-seq-{i}-{Guid.NewGuid():N}";
                var response = await restClient.PostAsJsonAsync(
                    $"/rooms/{roomId}/messages",
                    new SendMessageRequest(content),
                    TestContext.Current.CancellationToken);

                Assert.True(response.IsSuccessStatusCode);
            });

            await Task.WhenAll(sendTasks);

            var timeout = Stopwatch.StartNew();
            while (receivedDeliveries.Count < expectedDeliveries && timeout.Elapsed < TimeSpan.FromSeconds(15))
            {
                await Task.Delay(50, TestContext.Current.CancellationToken);
            }
            sw.Stop();

            Assert.Equal(expectedDeliveries, receivedDeliveries.Count);

            var distinctSenders = receivedDeliveries.Select(d => d.ClientId).Distinct().Count();
            Assert.Equal(clientCount, distinctSenders);
        }
        finally
        {
            foreach (var conn in connections)
            {
                try
                {
                    await conn.StopAsync(TestContext.Current.CancellationToken);
                    await conn.DisposeAsync();
                }
                catch
                {
                }
            }
        }
    }
}
