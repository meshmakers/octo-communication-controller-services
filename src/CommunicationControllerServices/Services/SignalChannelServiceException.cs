namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

/// <summary>
///     How a <see cref="ISignalChannelService"/> operation failed — the controller maps these onto
///     the HTTP contract (404 / 400 / 409 / 422 / 429).
/// </summary>
internal enum SignalChannelErrorKind
{
    /// <summary>No SignalChannel definition exists in the tenant → 404.</summary>
    NotFound = 0,

    /// <summary>Invalid input (E.164 number, verification code, missing ApiUrl) → 400.</summary>
    Validation = 1,

    /// <summary>Singleton / immutability / cross-tenant number claim violated → 409.</summary>
    Conflict = 2,

    /// <summary>The bridge rejected the call (its 400 <c>{"error": ...}</c>) → 400.</summary>
    BridgeRejected = 3,

    /// <summary>Signal rate limit (bridge 429) → 429 passthrough with Retry-After when known.</summary>
    BridgeRateLimited = 4,

    /// <summary>The bridge could not be reached at all → 400.</summary>
    BridgeUnreachable = 5,

    /// <summary>
    ///     Signal demands a captcha for this registration → 422. Machine-readable on purpose: the
    ///     Studio wizard attempts register WITHOUT a captcha first and only shows the captcha step
    ///     when this comes back. Otherwise behaves exactly like
    ///     <see cref="BridgeRejected"/> (state machine, history entry, error message).
    /// </summary>
    BridgeCaptchaRequired = 6
}

/// <summary>
///     Failure of a Signal-channel operation (AB#5143). Messages are tenant-facing and never
///     contain another tenant's identity — a cross-tenant number conflict only says the number is
///     taken, not by whom.
/// </summary>
internal class SignalChannelServiceException : Exception
{
    private SignalChannelServiceException(SignalChannelErrorKind kind, string message,
        Exception? inner = null) : base(message, inner)
    {
        Kind = kind;
    }

    /// <summary>How the operation failed.</summary>
    internal SignalChannelErrorKind Kind { get; }

    /// <summary>Retry-After for <see cref="SignalChannelErrorKind.BridgeRateLimited"/>, when the bridge sent one.</summary>
    internal TimeSpan? RetryAfter { get; private init; }

    internal static SignalChannelServiceException ChannelNotFound(string tenantId)
    {
        return new SignalChannelServiceException(SignalChannelErrorKind.NotFound,
            $"[{tenantId}] No Signal channel is defined for this tenant.");
    }

    internal static SignalChannelServiceException InvalidNumber(string? number)
    {
        return new SignalChannelServiceException(SignalChannelErrorKind.Validation,
            $"'{number}' is not a valid E.164 phone number. Expected '+' followed by 7 to 15 digits " +
            "(e.g. +436641234567).");
    }

    internal static SignalChannelServiceException InvalidVerificationCode()
    {
        return new SignalChannelServiceException(SignalChannelErrorKind.Validation,
            "The verification code must be the digits received by SMS (a dash is tolerated).");
    }

    internal static SignalChannelServiceException NoApiUrl()
    {
        return new SignalChannelServiceException(SignalChannelErrorKind.Validation,
            "No Signal bridge ApiUrl was provided and the instance defines no default " +
            "(CommunicationController:SignalBridgeApiUrl).");
    }

    internal static SignalChannelServiceException AlreadyRegistered(string tenantId, string number)
    {
        return new SignalChannelServiceException(SignalChannelErrorKind.Conflict,
            $"[{tenantId}] The Signal channel is already registered with '{number}'. The number is " +
            "immutable while registered — delete the channel first to register again.");
    }

    internal static SignalChannelServiceException NumberClaimedByOtherTenant(string number)
    {
        return new SignalChannelServiceException(SignalChannelErrorKind.Conflict,
            $"The phone number '{number}' is already claimed by another tenant on this instance.");
    }

    internal static SignalChannelServiceException NoVerificationPending(string tenantId)
    {
        return new SignalChannelServiceException(SignalChannelErrorKind.Conflict,
            $"[{tenantId}] No verification is pending — start a registration first " +
            "(POST signal/channel/register).");
    }

    internal static SignalChannelServiceException FromBridge(SignalBridgeException bridgeException)
    {
        var kind = bridgeException.Kind switch
        {
            SignalBridgeErrorKind.RateLimited => SignalChannelErrorKind.BridgeRateLimited,
            SignalBridgeErrorKind.Unreachable => SignalChannelErrorKind.BridgeUnreachable,
            SignalBridgeErrorKind.Rejected when bridgeException.IsCaptchaRequired =>
                SignalChannelErrorKind.BridgeCaptchaRequired,
            _ => SignalChannelErrorKind.BridgeRejected
        };

        return new SignalChannelServiceException(kind, bridgeException.Message, bridgeException)
        {
            RetryAfter = bridgeException.RetryAfter
        };
    }
}
