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

    public int MembershipChecks => _membershipChecks;

    public Task<string?> GetRoleAsync(string userId, CancellationToken ct) =>
        Task.FromResult(Roles.TryGetValue(userId, out var role) ? role : null);

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
