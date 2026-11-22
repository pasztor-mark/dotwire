using System.Diagnostics;
using OpenTelemetry;

namespace Dotwire.Configuration;

public sealed class TraceScrubber : BaseProcessor<Activity>
{
    internal static readonly HashSet<string> AllowedTags =
    [
        "http.request.method", "http.response.status_code", "http.route", "url.scheme",
        "network.protocol.version", "server.address", "server.port", "error.type",
        "rpc.system", "rpc.service", "rpc.method",
        "db.system.name", "db.operation.name", "db.response.status_code",
    ];

    public override void OnEnd(Activity activity)
    {
        foreach (var e in activity.Events)
            if (e.Tags.Any())
            {
                activity.ActivityTraceFlags &= ~ActivityTraceFlags.Recorded;
                return;
            }

        List<string>? drop = null;
        foreach (var tag in activity.TagObjects)
            if (!AllowedTags.Contains(tag.Key))
                (drop ??= []).Add(tag.Key);
        if (drop is not null)
            foreach (var key in drop)
                activity.SetTag(key, null);

        activity.SetStatus(activity.Status);
        activity.TraceStateString = null;
    }
}
