using Meshmakers.Octo.Backend.CommunicationControllerServices.Models;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

/// <summary>
///     Tenant self-service activation of a Signal phone number (AB#5143). Owns the singleton and
///     cross-tenant invariants of the <c>System.Communication/SignalChannel</c> entity:
///     <list type="bullet">
///         <item>exactly ONE SignalChannel per tenant,</item>
///         <item>a phone number is claimable by only one tenant per instance,</item>
///         <item>the number is immutable while <c>Registered</c> (delete first),</item>
///         <item>delete works from every state (best-effort bridge unregister).</item>
///     </list>
///     All failures are <see cref="SignalChannelServiceException"/>s; the controller maps their
///     <see cref="SignalChannelErrorKind"/> onto HTTP status codes.
///     <para>
///         Registration audit trail (WI rev 4): every register/verify/delete attempt that reaches
///         the channel definition — including bridge failures the API surfaces as 4xx/429 — is
///         recorded server-side on <c>SignalChannel.RegistrationHistory</c> (UTC timestamp, acting
///         user, action, outcome detail; newest first, newest 50 kept). The mutating methods take
///         the acting <see cref="SignalChannelActor"/> for exactly that record; clients can never
///         write history. Deleting the channel deletes its history with it.
///     </para>
/// </summary>
public interface ISignalChannelService
{
    /// <summary>
    ///     The tenant's channel including the live bridge cross-check
    ///     (<c>BridgeRegistered</c> / <c>Warning</c>), or <c>null</c> when no definition exists.
    /// </summary>
    Task<SignalChannelDto?> GetChannelAsync(string tenantId);

    /// <summary>
    ///     Creates (or re-uses) the singleton definition, claims the number, and starts the bridge
    ///     registration. On bridge success the state is <c>CodePending</c>; a bridge rejection
    ///     leaves the definition at <c>Failed</c> with <c>LastError</c> set (and still throws).
    ///     Audit: a <c>RegisterRequested</c> entry is appended with the persisted claim (before
    ///     the bridge call); a bridge failure additionally persists a <c>RegisterFailed</c> entry
    ///     even though the call throws. An optional display name is stored on the definition and
    ///     pushed to the bridge once the channel reaches <c>Registered</c>.
    /// </summary>
    Task<SignalChannelDto> RegisterAsync(string tenantId, SignalChannelActor actor, string? number, string? apiUrl,
        string? captchaToken, string? displayName = null);

    /// <summary>
    ///     Verifies the SMS code against the bridge. On success the state becomes
    ///     <c>Registered</c> with <c>RegisteredAt</c> = utcnow; on a bridge rejection the state
    ///     stays <c>CodePending</c> with <c>LastError</c> set (and still throws). Audit:
    ///     <c>CodeVerified</c> on success, a persisted <c>VerifyFailed</c> entry on a bridge
    ///     failure even though the call throws.
    /// </summary>
    Task<SignalChannelDto> VerifyAsync(string tenantId, SignalChannelActor actor, string? code);

    /// <summary>
    ///     Changes the profile display name WITHOUT re-registering. Non-empty: the attribute is
    ///     updated (persisted first, so a failed push can be retried by calling again); when the
    ///     channel is <c>Registered</c> the profile is additionally pushed to the bridge
    ///     (<c>PUT /v1/profiles/{number}</c>) — a push failure then surfaces as a bridge error
    ///     while the stored name is kept. Empty/whitespace/null clears the stored attribute
    ///     WITHOUT a bridge call (Signal profiles need a non-empty name; a previously pushed
    ///     profile name remains on the account). Audit: a <c>ProfileUpdated</c> entry carrying
    ///     the pushed name, the failure detail, a stored-only note when not registered, or the
    ///     cleared note.
    /// </summary>
    Task<SignalChannelDto> SetDisplayNameAsync(string tenantId, SignalChannelActor actor, string? displayName);

    /// <summary>
    ///     Deletes the definition from every state. The bridge unregister
    ///     (<c>delete_local_data</c>) is best-effort — a "not registered" answer or an unreachable
    ///     bridge never blocks the delete; the number becomes claimable again. Audit: a successful
    ///     delete erases the definition INCLUDING its history by design (fresh definition = fresh
    ///     history; the system event trail still records the deletion), so no persisted
    ///     <c>Deleted</c> entry survives; a failed repository delete leaves the definition in
    ///     place with a persisted <c>DeleteFailed</c> entry.
    /// </summary>
    Task DeleteChannelAsync(string tenantId, SignalChannelActor actor);
}
