using System.Net;
using Dotwire.Configuration;

namespace Dotwire.Nats;

public static class WebhookUrlRules
{
    public static void Validate(WebhooksOptions options)
    {
        Check(options.Presend.Url, "Dotwire:Webhooks:Presend:Url", options.AllowInsecureUrls);
        Check(options.PostSend.Url, "Dotwire:Webhooks:PostSend:Url", options.AllowInsecureUrls);
    }

    private static void Check(string? url, string name, bool allowInsecure)
    {
        if (string.IsNullOrEmpty(url))
            return;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            throw new InvalidOperationException($"{name} must be an absolute http(s) URL.");

        var loopback = uri.IsLoopback || (IPAddress.TryParse(uri.DnsSafeHost, out var ip) && IPAddress.IsLoopback(ip));
        if (uri.Scheme == Uri.UriSchemeHttps || loopback || allowInsecure)
            return;

        throw new InvalidOperationException(
            $"{name} must use https (or a loopback host); set Dotwire:Webhooks:AllowInsecureUrls=true to override.");
    }
}
