using System.ComponentModel.DataAnnotations;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Models;

/// <summary>
///     Body of <c>POST {tenantId}/v1/signal/channel/verify</c> (AB#5143).
/// </summary>
/// <param name="Code">
///     The SMS verification code Signal sent to the number (digits, an optional dash as received
///     in the SMS is tolerated).
/// </param>
public sealed record VerifySignalChannelRequestDto([Required] string Code);
