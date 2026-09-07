using System.ComponentModel.DataAnnotations;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Models;

/// <summary>
///     Body of <c>POST {tenantId}/v1/signal/channel/register</c> (AB#5143).
/// </summary>
/// <param name="Number">Phone number to claim and register, E.164 (+ followed by digits).</param>
/// <param name="ApiUrl">
///     Optional bridge base URL. Defaults to the existing definition's ApiUrl, then to the
///     instance-wide <c>CommunicationController:SignalBridgeApiUrl</c> setting.
/// </param>
/// <param name="CaptchaToken">
///     Optional captcha token from signalcaptchas.org (starts with <c>signalcaptcha://</c>) —
///     Signal demands one for most fresh registrations.
/// </param>
/// <param name="DisplayName">
///     Optional profile display name, stored on the definition and pushed to the bridge
///     (<c>PUT /v1/profiles/{number}</c>) once the channel reaches <c>Registered</c> — without
///     it, Signal users see the number as "Unknown". Changeable later without re-registering via
///     <c>PUT signal/channel/displayName</c>.
/// </param>
public sealed record RegisterSignalChannelRequestDto(
    [Required] string Number,
    string? ApiUrl = null,
    string? CaptchaToken = null,
    string? DisplayName = null);
