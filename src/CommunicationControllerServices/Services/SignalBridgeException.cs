namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

/// <summary>
///     How a call to the <c>signal-cli-rest-api</c> bridge failed.
/// </summary>
internal enum SignalBridgeErrorKind
{
    /// <summary>The bridge answered with an error status (400 with <c>{"error": "..."}</c>, or any other non-success).</summary>
    Rejected = 0,

    /// <summary>The bridge answered 429 — Signal rate limit. Retry later (see <see cref="SignalBridgeException.RetryAfter"/>).</summary>
    RateLimited = 1,

    /// <summary>The bridge could not be reached at all (connection failure or timeout).</summary>
    Unreachable = 2
}

/// <summary>
///     Failure of a <see cref="ISignalBridgeClient"/> call. The message is safe to surface to the
///     tenant — it carries the bridge's <c>error</c> text, never credentials (the bridge API has
///     none).
/// </summary>
internal class SignalBridgeException : Exception
{
    private SignalBridgeException(SignalBridgeErrorKind kind, string message, Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
    }

    /// <summary>How the call failed.</summary>
    internal SignalBridgeErrorKind Kind { get; }

    /// <summary>The bridge's Retry-After on a 429, when present.</summary>
    internal TimeSpan? RetryAfter { get; private init; }

    internal static SignalBridgeException Rejected(int statusCode, string error)
    {
        return new SignalBridgeException(SignalBridgeErrorKind.Rejected,
            $"The Signal bridge rejected the request (HTTP {statusCode}): {error}");
    }

    internal static SignalBridgeException RateLimited(string error, TimeSpan? retryAfter)
    {
        return new SignalBridgeException(SignalBridgeErrorKind.RateLimited,
            $"The Signal service is rate limiting requests (HTTP 429): {error}")
        {
            RetryAfter = retryAfter
        };
    }

    internal static SignalBridgeException Unreachable(string apiUrl, Exception inner)
    {
        return new SignalBridgeException(SignalBridgeErrorKind.Unreachable,
            $"The Signal bridge at '{apiUrl}' could not be reached: {inner.Message}", inner);
    }
}
