using Dotwire.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace dotwire.Tests;

public class LogSwitchesTests
{
    private sealed class CaptureProvider(List<LogLevel> seen) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new CaptureLogger(seen);

        public void Dispose()
        {
        }

        private sealed class CaptureLogger(List<LogLevel> seen) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (seen) seen.Add(logLevel);
            }
        }
    }

    private static List<LogLevel> Emit(bool disableDebug, bool disableAll)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Logging:LogLevel:Default"] = "Information",
                ["Logging:LogLevel:Microsoft.AspNetCore"] = "Warning",
            })
            .Build();
        var seen = new List<LogLevel>();
        using var provider = new ServiceCollection()
            .AddLogging(b =>
            {
                b.AddConfiguration(config.GetSection("Logging"));
                LogSwitches.Apply(b, new DotwireOptions { DisableDebugLogging = disableDebug, DisableAllLogging = disableAll });
                b.AddProvider(new CaptureProvider(seen));
            })
            .BuildServiceProvider();

        var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("Dotwire.Test");
        logger.LogTrace("t");
        logger.LogDebug("d");
        logger.LogInformation("i");
        logger.LogWarning("w");
        logger.LogError("e");
        logger.LogCritical("c");
        return seen;
    }

    [Fact]
    public void Defaults_KeepConfiguredLevels()
    {
        Assert.Equal([LogLevel.Information, LogLevel.Warning, LogLevel.Error, LogLevel.Critical], Emit(false, false));
    }

    [Fact]
    public void DisableDebugLogging_KeepsWarningAndAbove()
    {
        Assert.Equal([LogLevel.Warning, LogLevel.Error, LogLevel.Critical], Emit(true, false));
    }

    [Fact]
    public void DisableAllLogging_EmitsNothing()
    {
        Assert.Empty(Emit(false, true));
    }

    [Fact]
    public void DisableAllLogging_WinsOverDisableDebugLogging()
    {
        Assert.Empty(Emit(true, true));
    }

    [Fact]
    public void Defaults_BothFlagsOff()
    {
        var options = new DotwireOptions();

        Assert.False(options.DisableDebugLogging);
        Assert.False(options.DisableAllLogging);
    }
}
