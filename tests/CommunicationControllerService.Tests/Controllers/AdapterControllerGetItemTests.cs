using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Backend.CommunicationControllerServices.TenantApi.v1.Controllers;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Controllers;

/// <summary>
/// AB#5537 — <c>GET {tenantId}/v1/adapter/{adapterRtEntityId}</c> runs under the read-only policy,
/// so it must never ask for revealed secrets: plaintext only travels over the adapter's own channel.
/// </summary>
internal class AdapterControllerGetItemTests
{
    private const string TenantId = "acme";

    [Test]
    public async Task GetItem_NeverRequestsRevealedSecrets()
    {
        var adapters = Substitute.For<IAdapterService>();
        var sut = new AdapterController(NullLogger<AdapterController>.Instance,
            Substitute.For<ICommunicationRepository>(), adapters,
            Substitute.For<ITenantLendingScopeResolver>());
        var httpContext = new DefaultHttpContext();
        httpContext.Request.RouteValues["tenantId"] = TenantId;
        sut.ControllerContext = new ControllerContext { HttpContext = httpContext };

        var adapterRtEntityId = RtEntityCreator.CreateAdapter().ToRtEntityId();
        adapters.GetAdapterConfigurationAsync(TenantId, adapterRtEntityId, Arg.Any<bool>(), Arg.Any<bool>())
            .Returns(new AdapterConfigurationDto(adapterRtEntityId, null, []));

        var result = await sut.GetItem(adapterRtEntityId);

        await Assert.That(result).IsTypeOf<OkObjectResult>();
        await adapters.Received(1).GetAdapterConfigurationAsync(TenantId, adapterRtEntityId, false, false);
        await adapters.DidNotReceive().GetAdapterConfigurationAsync(Arg.Any<string>(), Arg.Any<RtEntityId>(),
            Arg.Any<bool>(), true);
    }
}
