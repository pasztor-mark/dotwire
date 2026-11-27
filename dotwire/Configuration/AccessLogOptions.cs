namespace Dotwire.Configuration;

public sealed class AccessLogOptions
{
    public const string SectionName = "Dotwire:AccessLog";

    public bool Enabled { get; set; } = false;

    public int RetentionDays { get; set; } = 0;
}
