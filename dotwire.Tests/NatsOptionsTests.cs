using Dotwire.Configuration;
using Xunit;

namespace dotwire.Tests;

public class NatsOptionsTests
{
    [Fact]
    public void DefaultsMatchPreImplementationValues()
    {
        var options = new NatsOptions();

        // docs/pre-implementation.md §1.6 . provisioning values live in config, not literals.
        Assert.True(options.Enabled);
        Assert.Equal("room.", options.RoomSubjectPrefix);
        // spec §3.1: room.* matches exactly one token after room., keeping .live and
        // .ephemeral.* (core-only) off the JetStream-backed ROOMS stream.
        Assert.Equal("room.*", options.RoomsSubjectFilter);
        Assert.Equal(".live", options.RoomLiveSubjectSuffix);
        Assert.Equal(500, options.BatchMaxMessages);
        Assert.Equal(1000, options.BatchLingerMs);
        Assert.Equal("ROOMS", options.RoomsStream);
        Assert.Equal(48, options.RoomsMaxAgeHours);
        Assert.Equal("postgres-writer", options.PostgresWriterConsumer);
    }

    [Fact]
    public void ErasureNoticeDefaultsMatchSpec()
    {
        var options = new NatsOptions();

        Assert.Equal("ERASURES", options.ErasureNoticesStream);
        Assert.Equal("dotwire.erasures", options.ErasureNoticesSubject);
        Assert.Equal(168, options.ErasureNoticesMaxAgeHours);
        Assert.Equal("erasure-notifier", options.ErasureNoticesConsumer);
    }

    [Fact]
    public void AccessLogDefaultsMatchSpec()
    {
        var options = new NatsOptions();

        Assert.Equal("ACCESS", options.AccessLogStream);
        Assert.Equal("dotwire.access", options.AccessLogSubject);
        Assert.Equal("access-writer", options.AccessLogWriterConsumer);
        Assert.Equal(1073741824L, options.AccessLogMaxBytes);
        Assert.Equal(500, options.AccessLogWriter.BatchMaxMessages);
        Assert.Equal(1000, options.AccessLogWriter.BatchLingerMs);
    }
}
