using System.Net.Sockets;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Xunit;

namespace dotwire.Tests;

[Collection("Compose")]
public class ComplianceSqlComposeTests
{
    private readonly WebApplicationFactory<Program> _baseFactory;
    private readonly string _ownerConnectionString;
    private readonly string _appConnectionString;

    public ComplianceSqlComposeTests()
    {
        SkipUnlessReachable("127.0.0.1", 5432, "Postgres");
        SkipUnlessReachable("127.0.0.1", 4222, "NATS");

        DotNetEnv.Env.TraversePath().Load();
        var pgPassword = Environment.GetEnvironmentVariable("POSTGRES_PASSWORD")
            ?? throw new InvalidOperationException("POSTGRES_PASSWORD missing from .env");
        var appPassword = Environment.GetEnvironmentVariable("POSTGRES_APP_PASSWORD")!;
        _ownerConnectionString = $"Host=127.0.0.1;Port=5432;Database=dotwire;Username=dotwire;Password={pgPassword}";
        _appConnectionString = $"Host=127.0.0.1;Port=5432;Database=dotwire;Username=dotwire_app;Password={appPassword}";
        _baseFactory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.WithoutErasureWorker();
            b.UseSetting("ConnectionStrings:PostgresMigrator", _ownerConnectionString);
            b.UseSetting("ConnectionStrings:PostgresWrite", _appConnectionString);
            b.UseSetting("ConnectionStrings:PostgresRead", _appConnectionString);
            b.UseSetting("Postgres:AppRolePassword", appPassword);
            b.UseSetting("Postgres:Migrate", "true");
            b.UseSetting("Nats:Url", "nats://127.0.0.1:4222");
            b.UseSetting("Nats:Enabled", "true");
            b.UseSetting("Auth:Issuer", TestTokens.Issuer);
            b.UseSetting("Auth:Audience", TestTokens.Audience);
            b.UseSetting($"Auth:Keys:{TestKeys.Kid}", TestKeys.PublicPem);
            b.UseSetting("Dotwire:Encryption:ActiveKeyId", "it");
            b.UseSetting("Dotwire:Encryption:Keys:it", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        });
        _ = _baseFactory.Services;
    }

    private static void SkipUnlessReachable(string host, int port, string name)
    {
        try
        {
            using var client = new TcpClient();
            if (!client.ConnectAsync(host, port).Wait(TimeSpan.FromSeconds(1)))
                Assert.Skip($"{name} not reachable on {host}:{port} . run: docker compose up -d postgres nats");
        }
        catch (Exception)
        {
            Assert.Skip($"{name} not reachable on {host}:{port} . run: docker compose up -d postgres nats");
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<NpgsqlConnection> OpenAsync(string cs)
    {
        var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync(Ct);
        return conn;
    }

    private async Task<T> ScalarAsync<T>(string cs, string sql, params object[] args)
    {
        await using var conn = await OpenAsync(cs);
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var a in args) cmd.Parameters.AddWithValue(a);
        return (T)(await cmd.ExecuteScalarAsync(Ct))!;
    }

    private async Task ExecAsync(string cs, string sql, params object[] args)
    {
        await using var conn = await OpenAsync(cs);
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var a in args) cmd.Parameters.AddWithValue(a);
        await cmd.ExecuteNonQueryAsync(Ct);
    }

    private Task CleanHoldsAsync() => ExecAsync(_ownerConnectionString,
        "INSERT INTO legal_hold_releases (hold_id, released_by) SELECT id, 'test' FROM legal_holds h WHERE NOT EXISTS (SELECT 1 FROM legal_hold_releases r WHERE r.hold_id = h.id)");

    private async Task<(Guid room, long seq)> SeedMessageAsync(string sender, DateTime? time = null)
    {
        var room = Guid.NewGuid();
        var seq = Random.Shared.NextInt64(1, long.MaxValue);
        await ExecAsync(_ownerConnectionString,
            "INSERT INTO messages (room_id, time, seq, sender_id, key_id, content) VALUES ($1, $2, $3, $4, 'k', '\\x01')",
            room, time ?? DateTime.UtcNow.AddDays(-10), seq, sender);
        return (room, seq);
    }

    private Task<int> RedactCheckedAsync(Guid room, long seq, int minKeep, bool allow, string? hintSender = null, DateTime? hintTime = null) =>
        ScalarAsync<int>(_appConnectionString,
            "SELECT redact_message_checked($1, $2, $3, $4, $5, $6)",
            room, seq, minKeep, allow,
            (object?)hintSender ?? DBNull.Value,
            hintTime is null ? DBNull.Value : hintTime.Value);

    private Task PlaceRoomHoldAsync(Guid room) => ExecAsync(_ownerConnectionString,
        "INSERT INTO legal_holds (scope, room_id, matter_ref, placed_by) VALUES ('room', $1, 'm', 'test')", room);

    private Task PlaceUserHoldAsync(string user) => ExecAsync(_ownerConnectionString,
        "INSERT INTO legal_holds (scope, user_id, matter_ref, placed_by) VALUES ('user', $1, 'm', 'test')", user);

    private Task<bool> TombstoneExistsAsync(Guid room, long seq) => ScalarAsync<bool>(_ownerConnectionString,
        "SELECT EXISTS (SELECT 1 FROM message_redactions WHERE room_id = $1 AND seq = $2)", room, seq);

    private Task<long> MessageCountAsync(Guid room, long seq) => ScalarAsync<long>(_ownerConnectionString,
        "SELECT count(*) FROM messages WHERE room_id = $1 AND seq = $2", room, seq);

    private static readonly string RunUser = "sqlt-" + Guid.NewGuid().ToString("N");

    public static TheoryData<string, string> Tables => new()
    {
        { "legal_holds", $"INSERT INTO legal_holds (scope, user_id, matter_ref, placed_by) VALUES ('user', '{RunUser}', 'm', 'p')" },
        { "legal_hold_releases", "INSERT INTO legal_hold_releases (hold_id, released_by) SELECT max(id), 'p' FROM legal_holds" },
        { "erasures", $"INSERT INTO erasures (user_id, mode, requested_by) VALUES ('{RunUser}', 'anonymize', 'p')" },
        { "erasure_revocations", "INSERT INTO erasure_revocations (erasure_id, lsn) SELECT max(id), pg_current_wal_lsn() FROM erasures" },
        { "erasure_completions", "INSERT INTO erasure_completions (erasure_id) SELECT max(id) FROM erasures" },
    };

    private static string UpdateColumn(string table) => table switch
    {
        "legal_hold_releases" => "released_by",
        "erasure_revocations" or "erasure_completions" => "erasure_id",
        _ => "id",
    };

    [Theory]
    [MemberData(nameof(Tables))]
    public async Task NewTables_AreAppendOnly_ForAppAndOwner(string table, string insert)
    {
        await ExecAsync(_ownerConnectionString, $"INSERT INTO erasures (user_id, mode, requested_by) VALUES ('{RunUser}-seed', 'anonymize', 'p')");
        await ExecAsync(_ownerConnectionString, $"INSERT INTO legal_holds (scope, user_id, matter_ref, placed_by) VALUES ('user', '{RunUser}-seed', 'm', 'p')");
        try
        {
            await ExecAsync(_appConnectionString, insert);
        }
        finally
        {
            await CleanHoldsAsync();
        }

        foreach (var cs in new[] { _ownerConnectionString, _appConnectionString })
        {
            await Assert.ThrowsAsync<PostgresException>(() => ExecAsync(cs, $"UPDATE {table} SET {UpdateColumn(table)} = {UpdateColumn(table)}"));
            await Assert.ThrowsAsync<PostgresException>(() => ExecAsync(cs, $"DELETE FROM {table}"));
            await Assert.ThrowsAsync<PostgresException>(() => ExecAsync(cs, $"TRUNCATE {table} CASCADE"));
        }
    }

    [Fact]
    public async Task RedactChecked_RoomHold_ReturnsMinus1_AndKeepsRow()
    {
        var (room, seq) = await SeedMessageAsync("u1");
        await PlaceRoomHoldAsync(room);
        Assert.Equal(-1, await RedactCheckedAsync(room, seq, 0, true));
        Assert.Equal(1L, await MessageCountAsync(room, seq));
        Assert.False(await TombstoneExistsAsync(room, seq));
        await CleanHoldsAsync();
    }

    [Fact]
    public async Task RedactChecked_UserHold_UsesSenderHint_WhenRowMissing()
    {
        var hint = "hint-" + Guid.NewGuid();
        await PlaceUserHoldAsync(hint);
        var room = Guid.NewGuid();
        Assert.Equal(-1, await RedactCheckedAsync(room, 5, 0, true, hint, DateTime.UtcNow.AddDays(-10)));
        Assert.False(await TombstoneExistsAsync(room, 5));
        await CleanHoldsAsync();
    }

    [Fact]
    public async Task RedactChecked_MinKeep_ReturnsMinus2()
    {
        var (room, seq) = await SeedMessageAsync("u1", DateTime.UtcNow);
        Assert.Equal(-2, await RedactCheckedAsync(room, seq, 1, true));
        Assert.Equal(1L, await MessageCountAsync(room, seq));
    }

    [Fact]
    public async Task RedactChecked_ChangesNotAllowed_ReturnsMinus3()
    {
        var (room, seq) = await SeedMessageAsync("u1");
        Assert.Equal(-3, await RedactCheckedAsync(room, seq, 0, false));
        Assert.Equal(1L, await MessageCountAsync(room, seq));
    }

    [Fact]
    public async Task RedactChecked_ReleasedHold_Removes()
    {
        var (room, seq) = await SeedMessageAsync("u1");
        await PlaceRoomHoldAsync(room);
        await CleanHoldsAsync();
        Assert.Equal(1, await RedactCheckedAsync(room, seq, 0, true));
        Assert.Equal(0L, await MessageCountAsync(room, seq));
        Assert.True(await TombstoneExistsAsync(room, seq));
    }

    private Task<int> ReattributeAsync(Guid room, long seq, string oldSender) =>
        ScalarAsync<int>(_appConnectionString,
            "SELECT reattribute_message($1, $2, $3, 'k2', '\\x0203', 0, true)", room, seq, oldSender);

    [Fact]
    public async Task Reattribute_WithoutOpenErasure_ReturnsMinus4()
    {
        var sender = "nobody-" + Guid.NewGuid();
        var (room, seq) = await SeedMessageAsync(sender);
        Assert.Equal(-4, await ReattributeAsync(room, seq, sender));
    }

    [Fact]
    public async Task Reattribute_OpenErasure_SetsReservedSender()
    {
        var sender = "erase-" + Guid.NewGuid();
        var (room, seq) = await SeedMessageAsync(sender);
        await ExecAsync(_appConnectionString, "INSERT INTO erasures (user_id, mode, requested_by) VALUES ($1, 'anonymize', 'p')", sender);
        Assert.Equal(1, await ReattributeAsync(room, seq, sender));
        Assert.Equal("dotwire:erased", await ScalarAsync<string>(_ownerConnectionString,
            "SELECT sender_id FROM messages WHERE room_id = $1 AND seq = $2", room, seq));
        Assert.Equal("k2", await ScalarAsync<string>(_ownerConnectionString,
            "SELECT key_id FROM messages WHERE room_id = $1 AND seq = $2", room, seq));
        Assert.Equal("0203", await ScalarAsync<string>(_ownerConnectionString,
            "SELECT encode(content, 'hex') FROM messages WHERE room_id = $1 AND seq = $2", room, seq));
    }

    [Fact]
    public async Task Reattribute_OtherSender_NoRows()
    {
        var sender = "erase-" + Guid.NewGuid();
        var (room, seq) = await SeedMessageAsync("someone-else");
        await ExecAsync(_appConnectionString, "INSERT INTO erasures (user_id, mode, requested_by) VALUES ($1, 'anonymize', 'p')", sender);
        Assert.Equal(0, await ReattributeAsync(room, seq, sender));
    }

    [Fact]
    public async Task SetRetention_WhileHoldActive_StaysPaused()
    {
        try
        {
            await PlaceUserHoldAsync("ret-" + Guid.NewGuid());
            await ExecAsync(_appConnectionString, "SELECT set_message_retention(30)");
            Assert.True(await ScalarAsync<bool>(_appConnectionString, "SELECT get_retention_paused()"));
            await CleanHoldsAsync();
            await ExecAsync(_appConnectionString, "SELECT sync_retention_schedule()");
            Assert.False(await ScalarAsync<bool>(_appConnectionString, "SELECT get_retention_paused()"));
        }
        finally
        {
            await CleanHoldsAsync();
            await ExecAsync(_appConnectionString, "SELECT set_message_retention(0)");
        }
    }

    [Fact]
    public async Task NewFunctions_DeniedToPublic()
    {
        foreach (var sig in new[]
                 {
                     "reattribute_message(uuid,bigint,text,text,bytea,integer,boolean)",
                     "redact_message_checked(uuid,bigint,integer,boolean,text,timestamptz)",
                     "sync_retention_schedule()",
                     "get_retention_paused()",
                     "retention_job_running()",
                 })
        {
            Assert.False(await ScalarAsync<bool>(_ownerConnectionString,
                "SELECT has_function_privilege('public', $1, 'EXECUTE')", sig), sig);
            Assert.True(await ScalarAsync<bool>(_ownerConnectionString,
                "SELECT has_function_privilege('dotwire_app', $1, 'EXECUTE')", sig), sig);
        }
    }

    [Fact]
    public async Task HoldPlacement_SerializesWithRedaction()
    {
        var (room, seq) = await SeedMessageAsync("u1");
        await using var a = await OpenAsync(_ownerConnectionString);
        await using var tx = await a.BeginTransactionAsync(Ct);
        await using (var lockCmd = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended('dotwire.legal_hold', 0))", a, tx))
            await lockCmd.ExecuteNonQueryAsync(Ct);
        await using (var ins = new NpgsqlCommand("INSERT INTO legal_holds (scope, room_id, matter_ref, placed_by) VALUES ('room', $1, 'm', 'test')", a, tx))
        {
            ins.Parameters.AddWithValue(room);
            await ins.ExecuteNonQueryAsync(Ct);
        }

        var b = Task.Run(() => RedactCheckedAsync(room, seq, 0, true), Ct);
        await Task.Delay(1000, Ct);
        Assert.False(b.IsCompleted);

        await tx.CommitAsync(Ct);
        Assert.Equal(-1, await b.WaitAsync(TimeSpan.FromSeconds(10), Ct));
        Assert.Equal(1L, await MessageCountAsync(room, seq));
        await CleanHoldsAsync();
    }

    [Fact]
    public async Task SetRetention_BlocksOnExclusiveHoldLock_ThenStaysPaused()
    {
        await using var a = await OpenAsync(_ownerConnectionString);
        await using var tx = await a.BeginTransactionAsync(Ct);
        try
        {
            await using (var lockCmd = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended('dotwire.legal_hold', 0))", a, tx))
                await lockCmd.ExecuteNonQueryAsync(Ct);
            await using (var ins = new NpgsqlCommand("INSERT INTO legal_holds (scope, user_id, matter_ref, placed_by) VALUES ('user', $1, 'm', 'test')", a, tx))
            {
                ins.Parameters.AddWithValue("lock-" + Guid.NewGuid());
                await ins.ExecuteNonQueryAsync(Ct);
            }

            var b = Task.Run(() => ExecAsync(_appConnectionString, "SELECT set_message_retention(30)"), Ct);
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (await ScalarAsync<long>(_ownerConnectionString,
                       "SELECT count(*) FROM pg_locks WHERE locktype = 'advisory' AND NOT granted") == 0)
            {
                Assert.True(DateTime.UtcNow < deadline, "set_message_retention never waited on the hold lock");
                Assert.False(b.IsCompleted);
                await Task.Delay(50, Ct);
            }
            Assert.False(b.IsCompleted);

            await tx.CommitAsync(Ct);
            await b.WaitAsync(TimeSpan.FromSeconds(10), Ct);
            Assert.True(await ScalarAsync<bool>(_appConnectionString, "SELECT get_retention_paused()"));
        }
        finally
        {
            await CleanHoldsAsync();
            await ExecAsync(_appConnectionString, "SELECT set_message_retention(0)");
        }
    }
}
