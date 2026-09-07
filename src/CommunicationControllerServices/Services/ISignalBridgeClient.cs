namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

/// <summary>
///     Typed access to the cluster-shared <c>signal-cli-rest-api</c> bridge
///     (bbernhard/signal-cli-rest-api, multi-account mode). The bridge API is unauthenticated and
///     cluster-internal — this controller service is the ONLY component allowed to call it
///     (AB#5143); the browser never does.
/// </summary>
/// <remarks>
///     Every method throws <see cref="SignalBridgeException"/> on failure:
///     <see cref="SignalBridgeErrorKind.Rejected"/> for a bridge 4xx/5xx answer (the bridge reports
///     <c>{"error": "..."}</c> on 400), <see cref="SignalBridgeErrorKind.RateLimited"/> for a 429
///     (Signal rate limit, optionally with Retry-After), and
///     <see cref="SignalBridgeErrorKind.Unreachable"/> when the bridge cannot be reached at all.
/// </remarks>
internal interface ISignalBridgeClient
{
    /// <summary>
    ///     <c>POST /v1/register/{number}</c> — starts the registration of a phone number. Signal
    ///     answers with an SMS verification code. The optional captcha token comes from
    ///     signalcaptchas.org and starts with <c>signalcaptcha://</c>.
    /// </summary>
    Task RegisterAsync(string apiUrl, string number, string? captchaToken,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     <c>POST /v1/register/{number}/verify/{token}</c> — verifies the SMS code and completes
    ///     the registration.
    /// </summary>
    Task VerifyAsync(string apiUrl, string number, string verificationCode,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     <c>POST /v1/unregister/{number}</c> with <c>{"delete_local_data": true}</c> — removes
    ///     the account from the bridge's local state so the number becomes claimable again.
    /// </summary>
    Task UnregisterAsync(string apiUrl, string number, CancellationToken cancellationToken = default);

    /// <summary>
    ///     <c>GET /v1/accounts</c> — the phone numbers currently registered on the bridge.
    /// </summary>
    Task<IReadOnlyList<string>> GetAccountsAsync(string apiUrl, CancellationToken cancellationToken = default);
}
