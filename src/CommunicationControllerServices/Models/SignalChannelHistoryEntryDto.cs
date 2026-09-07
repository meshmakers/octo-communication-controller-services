namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Models;

/// <summary>
///     One entry of the Signal channel registration audit trail (AB#5143), serialized camelCase as
///     <c>{at, user, action, detail}</c>. The trail is server-side only: the controller appends an
///     entry for every register/verify/delete attempt — including failures the API surfaces as
///     4xx/429 — and clients can never write it. Newest first, the newest 50 kept.
/// </summary>
/// <param name="At">UTC timestamp of the attempt.</param>
/// <param name="User">
///     The acting user ("Name (subject)", the single available claim, or "unknown").
/// </param>
/// <param name="Action">
///     <c>RtSignalRegistrationActionEnum</c> name: <c>RegisterRequested</c>,
///     <c>RegisterFailed</c>, <c>CodeVerified</c>, <c>VerifyFailed</c>, <c>Deleted</c> or
///     <c>DeleteFailed</c>.
/// </param>
/// <param name="Detail">
///     Outcome detail, e.g. the bridge message ("captcha required", "rate limited (retry after
///     30s)", "wrong code") or "registered" on a successful verification.
/// </param>
public sealed record SignalChannelHistoryEntryDto(
    DateTime At,
    string User,
    string Action,
    string? Detail);
