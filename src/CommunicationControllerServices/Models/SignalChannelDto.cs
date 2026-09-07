namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Models;

/// <summary>
///     Answer of the tenant Signal-channel endpoints (AB#5143,
///     <c>{tenantId}/v1/signal/channel</c>). Serialized camelCase for the Studio UI:
///     <c>{number, apiUrl, registrationState, registeredAt, lastError, bridgeRegistered, warning,
///     history}</c>.
/// </summary>
/// <param name="Number">The claimed phone number in E.164 format.</param>
/// <param name="ApiUrl">Base URL of the signal-cli-rest-api bridge the channel targets.</param>
/// <param name="RegistrationState">
///     <c>RtSignalRegistrationStateEnum</c> as an integer: 0 Unregistered, 1 CodePending,
///     2 Registered, 3 Failed.
/// </param>
/// <param name="RegisteredAt">UTC timestamp of the successful verification, when Registered.</param>
/// <param name="LastError">Last bridge error for a register/verify attempt, when any.</param>
/// <param name="BridgeRegistered">
///     Whether the number is present in the bridge's <c>GET /v1/accounts</c> — the ground truth
///     next to the persisted state. <c>null</c> when the bridge could not be asked (GET only; the
///     mutation endpoints do not re-query the bridge and report <c>null</c>).
/// </param>
/// <param name="Warning">
///     Human-readable warning when <paramref name="BridgeRegistered"/> is <c>null</c> because the
///     bridge was unreachable.
/// </param>
/// <param name="History">
///     Registration audit trail, newest first (the newest 50 attempts). Appended server-side for
///     every register/verify/delete attempt including failures; clients never write it. Deleting
///     the channel deletes the history with it — a fresh definition starts with a fresh history.
/// </param>
public sealed record SignalChannelDto(
    string Number,
    string ApiUrl,
    int RegistrationState,
    DateTime? RegisteredAt,
    string? LastError,
    bool? BridgeRegistered,
    string? Warning,
    IReadOnlyList<SignalChannelHistoryEntryDto> History);
