using System.Net;

namespace Dotwire.Host;

/// <summary>Coarse classification of a <see cref="DotwireApiException"/>, derived from the HTTP status and error code.</summary>
public enum DotwireErrorKind
{
    BadRequest,
    Unauthorized,
    Forbidden,
    NotFound,
    PresendRejected,
    RateLimited,
    Unavailable,
    Unknown,
}

/// <summary>Thrown when dotwire answers with a non-success status.</summary>
public sealed class DotwireApiException : Exception
{
    public HttpMethod Method { get; }
    public string Path { get; }
    public HttpStatusCode StatusCode { get; }
    public DotwireErrorKind Kind { get; }

    /// <summary>The body's `"error"` field, when present (spec §3.14 error contract).</summary>
    public string? ErrorCode { get; }

    /// <summary>The body's `"reason"` field, when present.</summary>
    public string? Reason { get; }

    /// <summary>Parsed from the `Retry-After` header on a 429 response.</summary>
    public TimeSpan? RetryAfter { get; }

    public DotwireApiException(
        HttpMethod method,
        string path,
        HttpStatusCode statusCode,
        string? errorCode = null,
        string? reason = null,
        TimeSpan? retryAfter = null)
        : base(BuildMessage(method, path, statusCode, errorCode, reason))
    {
        Method = method;
        Path = path;
        StatusCode = statusCode;
        ErrorCode = errorCode;
        Reason = reason;
        RetryAfter = retryAfter;
        Kind = Classify(statusCode, errorCode);
    }

    private static string BuildMessage(HttpMethod method, string path, HttpStatusCode statusCode, string? errorCode, string? reason)
    {
        var msg = $"dotwire {method} {path} failed: {(int)statusCode} {statusCode}";
        if (errorCode is not null)
            msg += $" ({errorCode})";
        if (reason is not null)
            msg += $": {reason}";
        return msg;
    }

    private static DotwireErrorKind Classify(HttpStatusCode statusCode, string? errorCode) => statusCode switch
    {
        HttpStatusCode.BadRequest => DotwireErrorKind.BadRequest,
        HttpStatusCode.Unauthorized => DotwireErrorKind.Unauthorized,
        HttpStatusCode.Forbidden => DotwireErrorKind.Forbidden,
        HttpStatusCode.NotFound => DotwireErrorKind.NotFound,
        HttpStatusCode.UnprocessableEntity when errorCode == "presend_rejected" => DotwireErrorKind.PresendRejected,
        HttpStatusCode.TooManyRequests => DotwireErrorKind.RateLimited,
        HttpStatusCode.ServiceUnavailable => DotwireErrorKind.Unavailable,
        _ => DotwireErrorKind.Unknown,
    };
}

/// <summary>Thrown by <see cref="DotwireHostClient.SendAsUserAsync"/> when the presend hook (local or server-side) rejects the message.</summary>
public sealed class PresendRejectedException(string reason) : Exception(reason)
{
    public string Reason { get; } = reason;
}

/// <summary>
/// The message WAS sent (see <see cref="Sent"/>); only the PostSend hook failed afterwards.
/// Kept distinct from a send failure so callers never retry a send that already succeeded.
/// </summary>
public sealed class PostSendException(SendMessageResult sent, Exception inner)
    : Exception("The message was sent, but the PostSend hook failed.", inner)
{
    public SendMessageResult Sent { get; } = sent;
}
