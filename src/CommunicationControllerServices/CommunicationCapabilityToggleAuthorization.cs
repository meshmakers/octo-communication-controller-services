using Meshmakers.Octo.Communication.Contracts;
using Meshmakers.Octo.Services.Infrastructure.Authorization;
using Microsoft.AspNetCore.Authorization;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices;

/// <summary>
///     The authorization of the communication enable and disable endpoints (AB#7319): the central scope-plus-role policy of
///     octo-common-services with the tenant role <see cref="CommonConstants.TenantManagementRole" />. The host also calls
///     <c>AddOctoTenantRoleAuthorization()</c>, which registers the handler and writes the reason into the <c>403</c> body.
/// </summary>
public static class CommunicationCapabilityToggleAuthorization
{
    /// <summary>
    ///     Registers <see cref="Constants.TenantCommunicationCapabilityTogglePolicy" />.
    /// </summary>
    public static AuthorizationOptions AddCapabilityTogglePolicy(this AuthorizationOptions options)
    {
        return options.AddTenantRolePolicy(Constants.TenantCommunicationCapabilityTogglePolicy, CommonConstants.TenantManagementRole);
    }
}
