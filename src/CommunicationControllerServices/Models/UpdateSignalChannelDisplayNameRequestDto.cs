namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Models;

/// <summary>
///     Body of <c>PUT {tenantId}/v1/signal/channel/displayName</c> (AB#5143): changes the profile
///     display name without re-registering. Non-empty: the attribute is updated and the profile is
///     pushed to the bridge (<c>PUT /v1/profiles/{number}</c>) when the channel is
///     <c>Registered</c>. Empty/whitespace (the UI sends <c>{"displayName": ""}</c> to clear):
///     clears the stored attribute WITHOUT a bridge call — Signal profiles need a non-empty name,
///     so a previously pushed profile name remains on the account. Deliberately NOT
///     <c>[Required]</c>: an empty string is a valid payload (clear).
/// </summary>
/// <param name="DisplayName">The profile display name Signal users should see; empty to clear.</param>
public sealed record UpdateSignalChannelDisplayNameRequestDto(
    string? DisplayName = null);
