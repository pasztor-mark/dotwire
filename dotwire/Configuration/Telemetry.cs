using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Dotwire.Configuration;

public static class Telemetry
{
    public static void Apply(IServiceCollection services, DotwireOptions options, string? serviceName)
    {
        services.AddMetrics();
        services.AddSingleton<DotwireMetrics>();
        if (options.DisableMetrics && !options.EnableTracing)
            return;

        var otel = services.AddOpenTelemetry()
            .ConfigureResource(r => r
                .AddService(ServiceName(serviceName))
                .AddAttributes([new KeyValuePair<string, object>("dotwire.node.role", options.Role.ToString())]));

        if (!options.DisableMetrics)
        {
            otel.WithMetrics(m => m.AddDotwireMeters().AddOtlpExporter());
            if (options.Role is NodeRole.All or NodeRole.Api)
                services.AddHostedService<Dotwire.Nats.JetStreamMetricsPoller>();
        }

        if (options.EnableTracing)
            otel.WithTracing(t => t.AddDotwireSources().AddOtlpExporter());
    }

    public static TracerProviderBuilder AddDotwireSources(this TracerProviderBuilder builder) => builder
        .AddAspNetCoreInstrumentation()
        .AddSource("Microsoft.AspNetCore.SignalR.Server", "System.Net.Http", "Npgsql")
        .AddProcessor(new TraceScrubber());

    public static MeterProviderBuilder AddDotwireMeters(this MeterProviderBuilder builder) => builder
        .AddMeter(
            DotwireMetrics.MeterName,
            "Microsoft.AspNetCore.Hosting",
            "Microsoft.AspNetCore.Server.Kestrel",
            "Microsoft.AspNetCore.Http.Connections",
            "Microsoft.AspNetCore.RateLimiting",
            "System.Net.Http",
            "System.Runtime",
            "Npgsql",
            "NATS.Net")
        .AddView("messaging.client.*", MetricStreamConfiguration.Drop)
        .AddView("nats.client.*", new MetricStreamConfiguration { TagKeys = [] })
        .AddView("db.client.*", new MetricStreamConfiguration
        {
            TagKeys = ["db.client.connection.state", "db.client.connection.pool.name", "db.operation.name", "error.type"],
        });

    internal static string ServiceName(string? configured) =>
        string.IsNullOrWhiteSpace(configured) ? "dotwire" : configured;
}
