using System.Security.Claims;
using Duende.IdentityModel;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.TenantApi.v1.Controllers;

/// <summary>
///     Claim reads that survive the JWT handler's inbound claim mapping (AB#5328).
/// </summary>
/// <remarks>
///     <para>
///         The bearer pipeline runs with <c>MapInboundClaims</c> enabled, so by the time a
///         <see cref="ClaimsPrincipal" /> exists the handler has already renamed the token's
///         <c>sub</c> to <see cref="ClaimTypes.NameIdentifier" />, <c>role</c> to
///         <see cref="ClaimTypes.Role" />, <c>email</c> to <see cref="ClaimTypes.Email" /> and
///         <c>name</c> to <see cref="ClaimTypes.Name" />. Code that asks for the raw JWT spelling
///         gets nothing back — and, because every one of these reads has a fallback, it gets
///         something plausible instead of an error.
///     </para>
///     <para>
///         🔴 That is how AB#5328 stayed invisible: <c>CallerSubjectId</c> quietly degraded to the
///         OAuth <c>client_id</c> and <c>CallerRoles</c> arrived empty, on a caller that carried a
///         subject and seventeen roles. Same lesson as
///         <see cref="PrincipalRoleExtensions" /> (AB#5111) and the AB#5030 finding on the MCP side:
///         always probe the mapped and the raw claim type together.
///     </para>
/// </remarks>
public static class PrincipalClaimExtensions
{
    /// <summary>
    ///     The caller's subject, under either spelling. Falls back to <c>client_id</c> last, which is
    ///     the honest answer for a client-credentials token: it has no subject.
    /// </summary>
    public static string? SubjectId(this ClaimsPrincipal principal)
    {
        return principal.FindFirstValue(JwtClaimTypes.Subject)
               ?? principal.FindFirstValue(ClaimTypes.NameIdentifier)
               ?? principal.FindFirstValue("client_id");
    }

    /// <summary>
    ///     The caller's display name, under either spelling, then <c>preferred_username</c> — which is
    ///     never mapped and is therefore what has been carrying this value so far.
    /// </summary>
    public static string? DisplayName(this ClaimsPrincipal principal)
    {
        return principal.FindFirstValue(JwtClaimTypes.Name)
               ?? principal.FindFirstValue(ClaimTypes.Name)
               ?? principal.FindFirstValue(JwtClaimTypes.PreferredUserName);
    }

    /// <summary>The caller's e-mail, under either spelling.</summary>
    public static string? Email(this ClaimsPrincipal principal)
    {
        return principal.FindFirstValue(JwtClaimTypes.Email)
               ?? principal.FindFirstValue(ClaimTypes.Email);
    }

    /// <summary>
    ///     Every role the principal carries, under either spelling, de-duplicated: a principal built
    ///     from a mapped token can hold both, and a role listed twice is not two roles.
    /// </summary>
    public static string[] RoleValues(this ClaimsPrincipal principal)
    {
        return principal.Claims
            .Where(claim => claim.Type == JwtClaimTypes.Role || claim.Type == ClaimTypes.Role)
            .Select(claim => claim.Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }
}
