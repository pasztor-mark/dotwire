namespace Dotwire.Configuration;

public static class LogSwitches
{
    public static void Apply(ILoggingBuilder logging, DotwireOptions options)
    {
        if (options.DisableAllLogging)
        {
            logging.ClearProviders();
            logging.SetMinimumLevel(LogLevel.None);
            logging.AddFilter(null, LogLevel.None);
            return;
        }

        if (options.DisableDebugLogging)
        {
            logging.SetMinimumLevel(LogLevel.Warning);
            logging.AddFilter(null, LogLevel.Warning);
        }
    }
}
