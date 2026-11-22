using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Headers;
using Dotwire.Api;
using Dotwire.Configuration;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Xunit;

namespace dotwire.Tests;

public class TelemetryTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static IMeterFactory NewScope() =>
        new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<IMeterFactory>();

    [Fact]
    public void Views_DropNatsMessagingAndStripTags()
    {
        var exported = new List<Metric>();
        using var provider = Sdk.CreateMeterProviderBuilder().AddDotwireMeters().AddInMemoryExporter(exported).Build();

        using (var nats = new Meter("NATS.Net"))
        {
            nats.CreateCounter<long>("messaging.client.published.messages")
                .Add(1, new KeyValuePair<string, object?>("messaging.destination.name", "room.abc"));
            nats.CreateCounter<long>("nats.client.reconnects")
                .Add(1, new KeyValuePair<string, object?>("messaging.destination.name", "room.abc"));
        }

        using (var npgsql = new Meter("Npgsql"))
        {
            npgsql.CreateHistogram<double>("db.client.operation.duration").Record(
                1,
                new KeyValuePair<string, object?>("db.query.text", "SELECT 1"),
                new KeyValuePair<string, object?>("db.operation.name", "SELECT"),
                new KeyValuePair<string, object?>("db.client.connection.pool.name", "Host=h;Username=u"),
                new KeyValuePair<string, object?>("db.npgsql.connection_id", "7"));
        }

        provider.ForceFlush();

        string[] allowed =
        [
            "db.client.connection.state", "db.client.connection.pool.name", "db.operation.name", "error.type",
        ];

        Assert.DoesNotContain(exported, m => m.Name == "messaging.client.published.messages");
        Assert.Contains(exported, m => m.Name == "nats.client.reconnects");
        Assert.Contains(exported, m => m.Name == "db.client.operation.duration");
        var dbKeys = exported.Where(m => m.Name == "db.client.operation.duration")
            .SelectMany(m => { var keys = new List<string>(); foreach (var p in m.GetMetricPoints()) foreach (var tg in p.Tags) keys.Add(tg.Key); return keys; })
            .ToList();
        Assert.DoesNotContain("db.query.text", dbKeys);
        Assert.DoesNotContain("db.npgsql.connection_id", dbKeys);
        Assert.Contains("db.operation.name", dbKeys);
        foreach (var metric in exported.Where(m => m.Name == "nats.client.reconnects"))
            foreach (var point in metric.GetMetricPoints())
                Assert.Equal(0, point.Tags.Count);
        foreach (var metric in exported.Where(m => m.Name == "db.client.operation.duration"))
            foreach (var point in metric.GetMetricPoints())
                foreach (var tag in point.Tags)
                    Assert.Contains(tag.Key, allowed);
    }

    [Fact]
    public void DataSources_ReportPoolName_NotConnectionString()
    {
        var host = "pool-name-probe-" + Guid.NewGuid().ToString("N");
        var poolNames = new List<string?>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "Npgsql" && instrument.Name == "db.client.connection.max")
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<int>((_, _, tags, _) =>
        {
            foreach (var tag in tags)
                if (tag.Key == "db.client.connection.pool.name")
                    lock (poolNames) poolNames.Add(tag.Value?.ToString());
        });
        listener.Start();

        using var read = Dotwire.Data.PostgresDataSources.Create($"Host={host};Username=u;Password=p", "read");
        listener.RecordObservableInstruments();

        Assert.Contains("read", poolNames);
        Assert.DoesNotContain(poolNames, n => n?.Contains(host) == true);
    }

    [Fact]
    public void EveryPostgresPool_IsNamed()
    {
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "dotwire.slnx")))
            repo = repo.Parent;
        var root = Path.Combine(repo!.FullName, "dotwire");
        var offenders = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .SelectMany(f => File.ReadLines(f).Select((line, i) => (File: Path.GetFileName(f), Line: i + 1, Text: line)))
            .Where(l => l.Text.Contains("new NpgsqlConnection(")
                || l.Text.Contains("NpgsqlDataSource.Create(")
                || (l.Text.Contains("new NpgsqlDataSourceBuilder(") && !l.Text.Contains("Name =")))
            .Select(l => $"{l.File}:{l.Line}")
            .ToList();

        Assert.True(Directory.Exists(root), root);
        Assert.Empty(offenders);
    }

    [Fact]
    public void Views_KeepDotwireTags()
    {
        var exported = new List<Metric>();
        using var provider = Sdk.CreateMeterProviderBuilder().AddDotwireMeters().AddInMemoryExporter(exported).Build();

        new DotwireMetrics(NewScope()).Rejection("test", "views_test", "views_test");
        provider.ForceFlush();

        var found = false;
        foreach (var metric in exported.Where(m => m.Name == "dotwire.rejections"))
            foreach (var point in metric.GetMetricPoints())
            {
                var tags = new Dictionary<string, object?>();
                foreach (var tag in point.Tags)
                    tags[tag.Key] = tag.Value;
                if (tags.Count == 3
                    && Equals(tags["transport"], "test")
                    && Equals(tags["reason"], "views_test")
                    && Equals(tags["operation"], "views_test")
                    && point.GetSumLong() == 1)
                    found = true;
            }

        Assert.True(found);
    }

    [Fact]
    public void ServiceName_DefaultsToDotwire()
    {
        Assert.Equal("dotwire", Telemetry.ServiceName(null));
        Assert.Equal("dotwire", Telemetry.ServiceName(" "));
        Assert.Equal("chat-prod", Telemetry.ServiceName("chat-prod"));
    }

    [Fact]
    public void MetricsOn_ByDefault()
    {
        var app = OfflineApp.Create(factory);
        Assert.NotNull(app.Services.GetService<MeterProvider>());
        Assert.NotNull(app.Services.GetService<DotwireMetrics>());
    }

    [Fact]
    public void Poller_RegisteredOnAllNodes()
    {
        var app = OfflineApp.Create(factory);
        Assert.Contains(app.Services.GetServices<IHostedService>(), s => s is Dotwire.Nats.JetStreamMetricsPoller);
    }

    [Fact]
    public void Poller_NotRegisteredOnGateway()
    {
        var app = OfflineApp.Create(factory, configure: b => b.UseSetting("Dotwire:Role", "Gateway"));
        Assert.DoesNotContain(app.Services.GetServices<IHostedService>(), s => s is Dotwire.Nats.JetStreamMetricsPoller);
    }

    [Fact]
    public void Poller_NotRegisteredWhenDisabled()
    {
        var app = OfflineApp.Create(factory, configure: b => b.UseSetting("Dotwire:DisableMetrics", "true"));
        Assert.DoesNotContain(app.Services.GetServices<IHostedService>(), s => s is Dotwire.Nats.JetStreamMetricsPoller);
    }

    [Fact]
    public void MetricsOff_WhenDisabled()
    {
        var app = OfflineApp.Create(factory, configure: b => b.UseSetting("Dotwire:DisableMetrics", "true"));
        Assert.Null(app.Services.GetService<MeterProvider>());
        Assert.NotNull(app.Services.GetService<DotwireMetrics>());
    }

    [Fact]
    public async Task HostDisposes_Promptly_WithMetricsOn()
    {
        var app = OfflineApp.Create(factory);
        var response = await app.CreateClient().GetAsync("/healthz", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var sw = Stopwatch.StartNew();
        await app.DisposeAsync();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"dispose took {sw.Elapsed}");
    }

    [Fact]
    public void MetricCapture_IsolatesScopes()
    {
        var a = NewScope();
        var b = NewScope();
        using var capA = new MetricCapture(a);
        using var capB = new MetricCapture(b);
        new DotwireMetrics(a).AuditUnavailable();
        Assert.Equal(1, capA.Sum("dotwire.audit.unavailable"));
        Assert.Equal(0, capB.Sum("dotwire.audit.unavailable"));
    }

    [Fact]
    public void TracingOff_ByDefault()
    {
        var app = OfflineApp.Create(factory);
        Assert.Null(app.Services.GetService<TracerProvider>());
    }

    [Fact]
    public void TracingOn_WhenEnabled_IndependentOfMetrics()
    {
        var app = OfflineApp.Create(factory, configure: b =>
        {
            b.UseSetting("Dotwire:EnableTracing", "true");
            b.UseSetting("Dotwire:DisableMetrics", "true");
        });
        Assert.NotNull(app.Services.GetService<TracerProvider>());
        Assert.Null(app.Services.GetService<MeterProvider>());
        Assert.DoesNotContain(app.Services.GetServices<IHostedService>(), s => s is Dotwire.Nats.JetStreamMetricsPoller);
    }

    [Fact]
    public void Scrubber_KeepsAllowlistedTags_AndDropsSpansWithTaggedEvents()
    {
        var exported = new List<Activity>();
        using var provider = Sdk.CreateTracerProviderBuilder().AddDotwireSources().AddInMemoryExporter(exported).Build();
        using var source = new ActivitySource("Npgsql");
        var marker = Guid.NewGuid().ToString("N");

        using (var ok = source.StartActivity("ok-" + marker, ActivityKind.Client)!)
        {
            ok.TraceStateString = "vendor=room-abc";
            ok.SetTag("db.query.text", "SELECT * FROM messages WHERE room_id = 'room-abc'");
            ok.SetTag("db.npgsql.data_source", "Host=h;Username=u");
            ok.SetTag("db.operation.name", "SELECT");
            ok.AddEvent(new ActivityEvent("received-first-response"));
            ok.SetStatus(ActivityStatusCode.Error, "invalid input syntax for type uuid: \"room-abc\"");
        }

        using (var failed = source.StartActivity("failed-" + marker, ActivityKind.Client)!)
            failed.AddException(new InvalidOperationException("invalid input syntax for type uuid: \"room-abc\""));

        var mine = exported.Where(a => a.DisplayName.EndsWith(marker)).ToList();
        var span = Assert.Single(mine);
        Assert.Equal("ok-" + marker, span.DisplayName);
        Assert.Equal(["db.operation.name"], span.TagObjects.Select(t => t.Key));
        Assert.Equal(ActivityStatusCode.Error, span.Status);
        Assert.Null(span.StatusDescription);
        Assert.Null(span.TraceStateString);
    }

    [Fact]
    public async Task Tracing_ServerSpan_CarriesRouteTemplate_NotIds()
    {
        var exported = new List<Activity>();
        using var provider = Sdk.CreateTracerProviderBuilder().AddDotwireSources().AddInMemoryExporter(exported).Build();
        var app = OfflineApp.Create(factory);
        var traceId = ActivityTraceId.CreateRandom();
        const string roomId = "trace-room-7f3a";

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/rooms/{roomId}/participants?u={roomId}");
        request.Headers.Add("traceparent", $"00-{traceId}-{ActivitySpanId.CreateRandom()}-01");
        request.Headers.Add("tracestate", $"vendor={roomId}");
        await app.CreateClient().SendAsync(request, Ct);
        bool Mine(Activity a) => a.TraceId == traceId && a.Source.Name == "Microsoft.AspNetCore";
        for (var i = 0; i < 100 && !exported.ToArray().Any(Mine); i++)
            await Task.Delay(20, Ct);

        var span = Assert.Single(exported.ToArray(), Mine);
        Assert.Equal("/rooms/{roomId}/participants", span.GetTagItem("http.route"));
        Assert.DoesNotContain(roomId, span.DisplayName);
        Assert.Null(span.TraceStateString);
        foreach (var tag in span.TagObjects)
        {
            Assert.Contains(tag.Key, TraceScrubber.AllowedTags);
            Assert.DoesNotContain(roomId, tag.Value?.ToString() ?? "");
        }
    }

    [Fact]
    public async Task MiddlewareRateLimit_DoesNotCountInDotwireRejections()
    {
        var app = OfflineApp.Create(factory, configure: b =>
        {
            b.UseSetting("Dotwire:RateLimits:Unauthenticated:PermitLimit", "1");
            b.UseSetting("Dotwire:RateLimits:Unauthenticated:TokensPerPeriod", "1");
            b.UseSetting("Dotwire:RateLimits:Unauthenticated:PeriodSeconds", "3600");
        });
        using var capture = new MetricCapture(app.Services.GetRequiredService<IMeterFactory>());
        var client = app.CreateClient();

        await client.GetAsync("/audit", Ct);
        var second = await client.GetAsync("/audit", Ct);

        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
        Assert.Equal(0, capture.Sum("dotwire.rejections"));
    }

    [Fact]
    public async Task FullVerifyGate_Busy_RecordsRejection()
    {
        var store = new FakeAccessStore();
        store.Roles["au1"] = "auditor";
        var app = OfflineApp.Create(factory, store);
        using var capture = new MetricCapture(app.Services.GetRequiredService<IMeterFactory>());
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Mint("au1", role: "auditor"));

        Assert.True(Audit.FullVerifyGate.Wait(0, Ct));
        HttpResponseMessage response;
        try
        {
            response = await client.GetAsync("/audit/verify?full=true", Ct);
        }
        finally
        {
            Audit.FullVerifyGate.Release();
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal(1, capture.Sum("dotwire.rejections", ("transport", "rest"), ("reason", "busy"), ("operation", "audit_verify")));
    }
}
