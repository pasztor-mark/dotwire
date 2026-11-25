namespace Dotwire.Configuration;

public sealed class CompressionOptions
{
    public const string SectionName = "Dotwire:Compression";

    public int? AfterDays { get; set; }
}
