using System.Text;
using System.Text.Json;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

/// <summary>
///     Reads the <c>exp</c> claim of a compact JWT without validating it (AB#5279). Validation is
///     not the point here — the token is forwarded, not trusted — the point is not to hand a member
///     a token that is already dead. Anything that is not a readable three-part JWT with a numeric
///     <c>exp</c> counts as "not expired": an opaque token is the identity service's business.
/// </summary>
internal static class JwtExpiry
{
    /// <summary>
    ///     Tolerance for clock skew between the controller and the identity service, applied in the
    ///     safe direction: a token that expires within this window is treated as expired, because the
    ///     member uses it seconds later and the issuer's clock may be ahead of ours.
    /// </summary>
    internal static readonly TimeSpan Skew = TimeSpan.FromSeconds(30);

    public static bool IsExpired(string token, DateTime nowUtc)
    {
        var expiresAt = TryReadExpiry(token);
        return expiresAt is not null && expiresAt.Value - Skew <= nowUtc;
    }

    public static DateTime? TryReadExpiry(string token)
    {
        var parts = token.Split('.');
        if (parts.Length != 3)
        {
            return null;
        }

        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var document = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
            if (!document.RootElement.TryGetProperty("exp", out var exp) || !exp.TryGetInt64(out var seconds))
            {
                return null;
            }

            return DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;
        }
        catch (Exception e) when (e is FormatException or JsonException or ArgumentException)
        {
            return null;
        }
    }
}
