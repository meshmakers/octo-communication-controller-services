using System.Security.Claims;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.TenantApi.v1.Controllers;

/// <summary>
///     Role check that survives the JWT handler's inbound claim mapping. The bearer pipeline sets
///     <c>TokenValidationParameters.RoleClaimType = "role"</c>, but with <c>MapInboundClaims</c>
///     enabled the handler renames the token's <c>role</c> claims to
///     <see cref="ClaimTypes.Role" /> before the principal is built — so
///     <see cref="ClaimsPrincipal.IsInRole" /> looks for a claim type that no longer exists and
///     silently answers <c>false</c> for every caller. The AB#5111 reconcile gate then withheld
///     declared roles from users who do carry UserManagement. Mirror of the AB#5030 lesson on the
///     MCP side (<c>sub</c> arriving as NameIdentifier): always probe the mapped and the raw claim
///     type together.
/// </summary>
public static class PrincipalRoleExtensions
{
    /// <summary>Whether the principal carries the role under any of the claim-type spellings.</summary>
    public static bool HasRole(this ClaimsPrincipal principal, string roleName)
    {
        return principal.IsInRole(roleName) ||
               principal.Claims.Any(c =>
                   (c.Type == ClaimTypes.Role || c.Type == "role") && c.Value == roleName);
    }
}
