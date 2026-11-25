namespace Dotwire.Configuration;

public static class CompressionRules
{
    private const string Key = "Dotwire:Compression:AfterDays";

    public static void Validate(CompressionOptions compression, NatsOptions nats)
    {
        if (compression.AfterDays is not { } days) return;
        if (days < 0 || days > 36500)
            throw new InvalidOperationException($"{Key} must be in the range 0..36500; got {days}.");
        if (days >= 1 && days * 24 <= nats.RoomsMaxAgeHours)
            throw new InvalidOperationException(
                $"{Key} ({days} days = {days * 24} hours) must exceed Nats:RoomsMaxAgeHours ({nats.RoomsMaxAgeHours}); the batch writer may still insert into chunks younger than that.");
    }
}
