using System.Diagnostics;
using Xunit;

namespace dotwire.Tests;

/// <summary>
/// Runs compose.yaml itself against brand-new named volumes, which is what a first install gets.
/// Uses a throwaway project and no published ports, so it can run beside a live stack. Cleanly
/// skipped when Docker isn't available.
/// </summary>
public class ComposeFileTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Nats_StartsOnAFreshVolume()
    {
        if ((await RunAsync("docker", ["compose", "version"], Directory.GetCurrentDirectory())).ExitCode != 0)
            Assert.Skip("docker compose not available");

        var root = RepoRoot();
        var project = $"dotwire-fresh-{Guid.NewGuid():N}";
        var noPorts = Path.Combine(Path.GetTempPath(), $"{project}.yaml");
        await File.WriteAllTextAsync(noPorts, "services:\n  nats:\n    ports: !reset []\n", Ct);
        string[] compose = ["compose", "-p", project, "-f", "compose.yaml", "-f", noPorts];

        try
        {
            var up = await RunAsync("docker", [.. compose, "up", "-d", "--wait", "--wait-timeout", "60", "nats"], root);
            var logs = await RunAsync("docker", [.. compose, "logs", "nats"], root);

            Assert.True(up.ExitCode == 0, $"nats did not become healthy:\n{up.Output}\n{logs.Output}");
        }
        finally
        {
            await RunAsync("docker", [.. compose, "down", "-v", "--remove-orphans"], root);
            File.Delete(noPorts);
        }
    }

    [Fact]
    public void ReadPool_HasAWarmFloor()
    {
        var line = File.ReadLines(Path.Combine(RepoRoot(), "compose.yaml")).Single(l => l.Contains("ConnectionStrings__PostgresRead"));
        Assert.Contains("Minimum Pool Size=20", line);
        Assert.Contains("Maximum Pool Size=100", line);
    }

    [Fact]
    public void AuthLookupKeys_PassedThroughOnEveryDotwireService()
    {
        string[] passthrough =
        [
            "Dotwire__AuthBatching__Enabled: \"${Dotwire__AuthBatching__Enabled:-false}\"",
            "Dotwire__AuthBatching__MaxInFlight: \"${Dotwire__AuthBatching__MaxInFlight:-2}\"",
            "Dotwire__AuthBatching__MaxBatchSize: \"${Dotwire__AuthBatching__MaxBatchSize:-512}\"",
            "Dotwire__AuthCacheSeconds: \"${Dotwire__AuthCacheSeconds:-0}\"",
        ];

        foreach (var file in new[] { "compose.yaml", "compose.multinode.yaml" })
        {
            var lines = File.ReadAllLines(Path.Combine(RepoRoot(), file));
            var services = lines.Count(l => l.Contains("Dotwire__Encryption__ActiveKeyId:"));

            Assert.True(services >= 1, $"{file} has no dotwire service");
            foreach (var entry in passthrough)
                Assert.Equal(services, lines.Count(l => l.Contains(entry)));
        }
    }

    [Fact]
    public void LoggingKeys_PassedThroughOnEveryDotwireService()
    {
        string[] passthrough =
        [
            "Dotwire__DisableDebugLogging: \"${Dotwire__DisableDebugLogging:-false}\"",
            "Dotwire__DisableAllLogging: \"${Dotwire__DisableAllLogging:-false}\"",
        ];

        foreach (var file in new[] { "compose.yaml", "compose.multinode.yaml" })
        {
            var lines = File.ReadAllLines(Path.Combine(RepoRoot(), file));
            var services = lines.Count(l => l.Contains("Dotwire__Encryption__ActiveKeyId:"));

            Assert.True(services >= 1, $"{file} has no dotwire service");
            foreach (var entry in passthrough)
                Assert.Equal(services, lines.Count(l => l.Contains(entry)));
        }
    }

    [Fact]
    public void EnvExample_ListsLoggingKeys()
    {
        var lines = File.ReadAllLines(Path.Combine(RepoRoot(), ".env.example"));

        Assert.Contains(lines, l => l.StartsWith("# Dotwire__DisableDebugLogging=false"));
        Assert.Contains(lines, l => l.StartsWith("# Dotwire__DisableAllLogging=false"));
    }

    [Fact]
    public void MetricsKeys_PassedThroughOnEveryDotwireService()
    {
        string[] passthrough =
        [
            "Dotwire__DisableMetrics: \"${Dotwire__DisableMetrics:-false}\"",
            "Dotwire__EnableTracing: \"${Dotwire__EnableTracing:-false}\"",
            "OTEL_EXPORTER_OTLP_ENDPOINT: \"${OTEL_EXPORTER_OTLP_ENDPOINT:-http://localhost:4317}\"",
        ];

        foreach (var file in new[] { "compose.yaml", "compose.multinode.yaml" })
        {
            var lines = File.ReadAllLines(Path.Combine(RepoRoot(), file));
            var services = lines.Count(l => l.Contains("Dotwire__Encryption__ActiveKeyId:"));

            Assert.True(services >= 1, $"{file} has no dotwire service");
            foreach (var entry in passthrough)
                Assert.Equal(services, lines.Count(l => l.Contains(entry)));
        }
    }

    [Fact]
    public void ObservabilityOverlay_PinsImage_KeepsGrafanaLocal_AndShipsValidDashboard()
    {
        var root = RepoRoot();
        var lines = File.ReadAllLines(Path.Combine(root, "compose.observability.yaml"));

        Assert.Contains(lines, l => l.Trim().StartsWith("image: grafana/otel-lgtm:") && !l.Contains(":latest"));
        Assert.All(lines.Where(l => l.Trim().StartsWith("- \"") && l.Contains(':')), l => Assert.Contains("127.0.0.1:", l));

        using var dashboard = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "observability", "grafana", "dotwire.json")));
        Assert.Equal("dotwire", dashboard.RootElement.GetProperty("uid").GetString());
        Assert.NotEmpty(dashboard.RootElement.GetProperty("panels").EnumerateArray());
    }

    [Fact]
    public void EnvExample_ListsMetricsKeys()
    {
        var lines = File.ReadAllLines(Path.Combine(RepoRoot(), ".env.example"));

        Assert.Contains(lines, l => l.StartsWith("# Dotwire__DisableMetrics=false"));
        Assert.Contains(lines, l => l.StartsWith("# Dotwire__EnableTracing=false"));
        Assert.Contains(lines, l => l.StartsWith("# OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4317"));
    }

    [Fact]
    public void EnvExample_ListsAuthLookupKeys()
    {
        var lines = File.ReadAllLines(Path.Combine(RepoRoot(), ".env.example"));

        Assert.Contains(lines, l => l.StartsWith("# Dotwire__AuthBatching__Enabled=false"));
        Assert.Contains(lines, l => l.StartsWith("# Dotwire__AuthBatching__MaxInFlight=2"));
        Assert.Contains(lines, l => l.StartsWith("# Dotwire__AuthBatching__MaxBatchSize=512"));
        Assert.Contains(lines, l => l.StartsWith("# Dotwire__AuthCacheSeconds=0"));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "compose.yaml")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("compose.yaml not found above the test output");
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(string file, string[] args, string workingDirectory)
    {
        var info = new ProcessStartInfo(file)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args)
            info.ArgumentList.Add(arg);

        // compose.yaml interpolates every service's required settings, even when starting only nats.
        foreach (var name in new[]
                 {
                     "POSTGRES_PASSWORD", "POSTGRES_APP_PASSWORD", "Dotwire__Encryption__Keys__k1",
                     "Auth__Issuer", "Auth__Audience", "Auth__Keys__dev",
                 })
            info.Environment[name] = "unused";

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        try
        {
            using var process = Process.Start(info)!;
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            return (process.ExitCode, await stdout + await stderr);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return (-1, ex.Message);
        }
    }
}
