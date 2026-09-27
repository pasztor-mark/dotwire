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
}
