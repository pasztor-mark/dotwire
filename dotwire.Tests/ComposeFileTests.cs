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
