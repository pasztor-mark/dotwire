using System.Collections.Concurrent;
using System.Security.Cryptography;
using Dotwire.Api;
using Dotwire.Auth;
using Dotwire.Nats;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace dotwire.Tests;

public sealed class FakeAccessStore : IAccessStore
{
    public ConcurrentDictionary<string, string> Roles { get; } = new(StringComparer.Ordinal);
    public ConcurrentDictionary<(Guid RoomId, string UserId), bool> Members { get; } = new();
    public Func<Guid, string, int, bool>? MembershipOverride { get; set; }
    private int _membershipChecks;
    private int _combinedChecks;
    private int _roleChecks;
    private int _batchChecks;

    public int MembershipChecks => _membershipChecks;
    public int CombinedChecks => _combinedChecks;
    public int RoleChecks => _roleChecks;
    public int BatchChecks => _batchChecks;

    public Task<IReadOnlyDictionary<(Guid RoomId, string UserId), (string? Role, bool IsMember)>> GetRolesAndMembershipsAsync(
        IReadOnlyCollection<(Guid RoomId, string UserId)> pairs, CancellationToken ct)
    {
        Interlocked.Increment(ref _batchChecks);
        return Task.FromResult<IReadOnlyDictionary<(Guid, string), (string?, bool)>>(
            pairs.Distinct().ToDictionary(
                p => p,
                p => (Roles.TryGetValue(p.UserId, out var role) ? role : null, Members.ContainsKey(p))));
    }

    public async Task<(string? Role, bool IsMember)> GetRoleAndMembershipAsync(Guid roomId, string userId, CancellationToken ct)
    {
        Interlocked.Increment(ref _combinedChecks);
        return (await GetRoleAsync(userId, ct), await IsMemberAsync(roomId, userId, ct));
    }

    public Task<string?> GetRoleAsync(string userId, CancellationToken ct)
    {
        Interlocked.Increment(ref _roleChecks);
        return Task.FromResult(Roles.TryGetValue(userId, out var role) ? role : null);
    }

    public Task<bool> IsMemberAsync(Guid roomId, string userId, CancellationToken ct)
    {
        var call = Interlocked.Increment(ref _membershipChecks);
        return Task.FromResult(MembershipOverride?.Invoke(roomId, userId, call) ?? Members.ContainsKey((roomId, userId)));
    }

    public Task<IReadOnlyDictionary<string, string>> GetRolesAsync(IReadOnlyCollection<string> userIds, CancellationToken ct) =>
        Task.FromResult<IReadOnlyDictionary<string, string>>(
            userIds.Where(Roles.ContainsKey).ToDictionary(u => u, u => Roles[u], StringComparer.Ordinal));

    public Task<IReadOnlySet<(Guid RoomId, string UserId)>> FilterMembersAsync(
        IReadOnlyCollection<(Guid RoomId, string UserId)> pairs, CancellationToken ct) =>
        Task.FromResult<IReadOnlySet<(Guid, string)>>(pairs.Where(Members.ContainsKey).ToHashSet());
}

public sealed class ThrowingAuditPublisher() : AuditPublisher(null!, Microsoft.Extensions.Options.Options.Create(new Dotwire.Configuration.NatsOptions()))
{
    public int Attempts;

    public override Task PublishAsync(AuditEvent evt, CancellationToken ct)
    {
        Interlocked.Increment(ref Attempts);
        throw new InvalidOperationException("audit stream unavailable");
    }
}

public static class OfflineApp
{
    public static WebApplicationFactory<Program> Create(
        WebApplicationFactory<Program> factory,
        IAccessStore? store = null,
        Action<IWebHostBuilder>? configure = null,
        Action<IServiceCollection>? services = null) =>
        factory.WithWebHostBuilder(b =>
        {
            b.WithoutErasureWorker();
            b.UseSetting("Postgres:Migrate", "false");
            b.UseSetting("ConnectionStrings:PostgresRead", "Host=localhost;Database=dotwire");
            b.UseSetting("ConnectionStrings:PostgresWrite", "Host=localhost;Database=dotwire");
            b.UseSetting("Nats:Enabled", "false");
            b.UseSetting("Auth:Issuer", TestTokens.Issuer);
            b.UseSetting("Auth:Audience", TestTokens.Audience);
            b.UseSetting($"Auth:Keys:{TestKeys.Kid}", TestKeys.PublicPem);
            b.UseSetting("Dotwire:Encryption:ActiveKeyId", "t1");
            b.UseSetting("Dotwire:Encryption:Keys:t1", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
            configure?.Invoke(b);
            b.ConfigureTestServices(s =>
            {
                if (store is not null)
                    s.AddSingleton(store);
                services?.Invoke(s);
            });
        });
}

public sealed class RecordingAccessLogPublisher() : AccessLogPublisher(
    null!,
    Microsoft.Extensions.Options.Options.Create(new Dotwire.Configuration.NatsOptions()),
    Microsoft.Extensions.Options.Options.Create(new Dotwire.Configuration.AccessLogOptions()),
    TestMetrics.Create().Metrics,
    Microsoft.Extensions.Logging.Abstractions.NullLogger<AccessLogPublisher>.Instance)
{
    public override bool IsEnabled => true;

    public List<AccessEvent> Recorded { get; } = [];

    public List<AccessEvent> Closes { get; } = [];

    public bool FailRecords { get; set; }

    public int FailFromCall { get; set; }

    public override Task RecordAsync(AccessEvent evt, CancellationToken ct)
    {
        int calls;
        lock (Recorded)
        {
            Recorded.Add(evt);
            calls = Recorded.Count;
        }
        if (!FailRecords && !(FailFromCall > 0 && calls >= FailFromCall))
            return Task.CompletedTask;
        if (evt.Event == AccessEvents.Opened)
            RecordClose(AccessEvents.Close(evt, AccessEvents.Error, DateTimeOffset.UtcNow));
        throw new AccessLogUnavailableException();
    }

    public override void RecordClose(AccessEvent evt)
    {
        lock (Closes)
            Closes.Add(evt);
    }
}
