using System.Diagnostics.Metrics;
using Dotwire.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace dotwire.Tests;

public sealed class MetricCapture : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly object _gate = new();
    private readonly List<(string Name, double Value, Dictionary<string, object?> Tags)> _all = [];

    public MetricCapture(IMeterFactory scope)
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == DotwireMetrics.MeterName && ReferenceEquals(instrument.Meter.Scope, scope))
                listener.EnableMeasurementEvents(instrument);
        };
        _listener.SetMeasurementEventCallback<long>((i, v, t, _) => Add(i, v, t));
        _listener.SetMeasurementEventCallback<int>((i, v, t, _) => Add(i, v, t));
        _listener.SetMeasurementEventCallback<double>((i, v, t, _) => Add(i, v, t));
        _listener.Start();
    }

    private void Add(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var dict = new Dictionary<string, object?>();
        foreach (var tag in tags)
            dict[tag.Key] = tag.Value;
        lock (_gate)
            _all.Add((instrument.Name, value, dict));
    }

    public long Sum(string instrument, params (string Key, string Value)[] tags)
    {
        lock (_gate)
            return (long)_all
                .Where(m => m.Name == instrument
                    && tags.All(t => m.Tags.TryGetValue(t.Key, out var v) && Equals(v?.ToString(), t.Value)))
                .Sum(m => m.Value);
    }

    public int Count(string instrument, params (string Key, string Value)[] tags)
    {
        lock (_gate)
            return _all.Count(m => m.Name == instrument
                && tags.All(t => m.Tags.TryGetValue(t.Key, out var v) && Equals(v?.ToString(), t.Value)));
    }

    public IReadOnlyList<(double Value, IReadOnlyDictionary<string, object?> Tags)> Observe(string instrument)
    {
        int start;
        lock (_gate)
            start = _all.Count;
        _listener.RecordObservableInstruments();
        lock (_gate)
            return _all.Skip(start)
                .Where(m => m.Name == instrument)
                .Select(m => (m.Value, (IReadOnlyDictionary<string, object?>)m.Tags))
                .ToList();
    }

    public void Dispose() => _listener.Dispose();
}

public static class TestMetrics
{
    public static (DotwireMetrics Metrics, IMeterFactory Scope) Create()
    {
        var scope = new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<IMeterFactory>();
        return (new DotwireMetrics(scope), scope);
    }
}
