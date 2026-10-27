using System.Reflection;
using System.Security.Claims;
using Meshmakers.Octo.Backend.CommunicationControllerServices;
using Meshmakers.Octo.Backend.CommunicationControllerServices.TenantApi.v1.Controllers;
using Meshmakers.Octo.Communication.Contracts;
using Meshmakers.Octo.Services.Infrastructure;
using Meshmakers.Octo.Services.Infrastructure.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Controllers;

/// <summary>
/// AB#7319: POST communication/enable and communication/disable need the tenant role TenantManagement on top of
/// the octo_api scope; a token without it is refused with a reason.
/// </summary>
internal class CommunicationControllerToggleAuthorizationTests
{
    private static ClaimsPrincipal Caller(params string[] roles)
    {
        var claims = new List<Claim>
        {
            new("tenant_id", "t"), new("sub", "s"),
            new(InfrastructureCommon.ClaimScope, CommonConstants.OctoApiFullAccess)
        };
        claims.AddRange(roles.Select(r => new Claim("role", r)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer", "name", "role"));
    }

    private static string PolicyOf(string action)
    {
        var attribute = typeof(CommunicationController).GetMethod(action)!.GetCustomAttributes<AuthorizeAttribute>().Single();
        return attribute.Policy!;
    }

    private static async Task<AuthorizationResult> Authorize(string policy, ClaimsPrincipal user)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorization(o => o.AddCapabilityTogglePolicy());
        services.AddOctoTenantRoleAuthorization();
        await using var provider = services.BuildServiceProvider();
        return await provider.GetRequiredService<IAuthorizationService>().AuthorizeAsync(user, policy);
    }

    [Test]
    [Arguments(nameof(CommunicationController.Enable))]
    [Arguments(nameof(CommunicationController.Disable))]
    public async Task Toggle_CarriesTheCapabilityTogglePolicy(string action)
    {
        await Assert.That(PolicyOf(action)).IsEqualTo(Constants.TenantCommunicationCapabilityTogglePolicy);
    }

    [Test]
    [Arguments(nameof(CommunicationController.Enable))]
    [Arguments(nameof(CommunicationController.Disable))]
    public async Task WithoutTheRole_IsRefusedWithAReason(string action)
    {
        var result = await Authorize(PolicyOf(action), Caller());

        await Assert.That(result.Succeeded).IsFalse();
        await Assert.That(result.Failure!.FailureReasons.Single().Message).Contains(CommonConstants.TenantManagementRole);
    }

    [Test]
    [Arguments(nameof(CommunicationController.Enable))]
    [Arguments(nameof(CommunicationController.Disable))]
    public async Task WithTenantManagement_IsAllowed(string action)
    {
        var result = await Authorize(PolicyOf(action), Caller(CommonConstants.TenantManagementRole));

        await Assert.That(result.Succeeded).IsTrue();
    }
}
