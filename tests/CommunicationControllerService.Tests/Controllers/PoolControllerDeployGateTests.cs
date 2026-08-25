using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Backend.CommunicationControllerServices.TenantApi.v1.Controllers;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects.ApiErrors;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Services.Infrastructure.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Controllers;

/// <summary>
/// AB#4255: the two endpoints that create operator-managed cluster resources refuse on a tenant
/// whose Communication is disabled, so nothing can be deployed behind the tenant delete guard's
/// back. Undeploy is deliberately not gated.
/// </summary>
internal class PoolControllerDeployGateTests
{
    private const string TenantId = "child-a";
    private static readonly OctoObjectId RtId = OctoObjectId.GenerateNewId();

    private static (PoolController sut, IPoolService pools, IConfigurationService configuration) CreateSut(bool enabled)
    {
        var pools = Substitute.For<IPoolService>();
        var configuration = Substitute.For<IConfigurationService>();
        configuration.IsEnabledAsync(TenantId).Returns(enabled);
        var sut = new PoolController(NullLogger<PoolController>.Instance, pools, configuration);
        var httpContext = new DefaultHttpContext();
        httpContext.Request.RouteValues["tenantId"] = TenantId;
        sut.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return (sut, pools, configuration);
    }

    [Test]
    public async Task DeployPool_ReturnsConflict_AndDoesNotDeploy_WhenCommunicationIsDisabled()
    {
        var (sut, pools, _) = CreateSut(enabled: false);

        var result = await sut.DeployPoolAsync(RtId);

        var conflict = result as ConflictObjectResult;
        await Assert.That(conflict).IsNotNull();
        await Assert.That((conflict!.Value as OperationFailedErrorDto)!.Message).Contains("Communication is disabled for tenant 'child-a'");
        await pools.DidNotReceive().DeployPoolAsync(Arg.Any<string>(), Arg.Any<OctoObjectId>());
    }

    [Test]
    public async Task DeployWorkload_ReturnsConflict_AndDoesNotDeploy_WhenCommunicationIsDisabled()
    {
        var (sut, pools, _) = CreateSut(enabled: false);

        var result = await sut.DeployWorkloadAsync(RtId);

        await Assert.That(result).IsTypeOf<ConflictObjectResult>();
        await pools.DidNotReceive().DeployWorkloadAsync(Arg.Any<string>(), Arg.Any<OctoObjectId>());
    }

    [Test]
    public async Task DeployPoolAndWorkload_PassThrough_WhenCommunicationIsEnabled()
    {
        var (sut, pools, _) = CreateSut(enabled: true);

        var poolResult = await sut.DeployPoolAsync(RtId);
        var workloadResult = await sut.DeployWorkloadAsync(RtId);

        await Assert.That(poolResult).IsTypeOf<NoContentResult>();
        await Assert.That(workloadResult).IsTypeOf<NoContentResult>();
        await pools.Received(1).DeployPoolAsync(TenantId, RtId);
        await pools.Received(1).DeployWorkloadAsync(TenantId, RtId);
    }

    [Test]
    public async Task UndeployPoolAndWorkload_StayOpen_WhenCommunicationIsDisabled()
    {
        var (sut, pools, configuration) = CreateSut(enabled: false);

        var poolResult = await sut.UndeployPoolAsync(RtId);
        var workloadResult = await sut.UndeployWorkloadAsync(RtId);

        await Assert.That(poolResult).IsTypeOf<NoContentResult>();
        await Assert.That(workloadResult).IsTypeOf<NoContentResult>();
        await pools.Received(1).UndeployPoolAsync(TenantId, RtId);
        await pools.Received(1).UndeployWorkloadAsync(TenantId, RtId);
        await configuration.DidNotReceive().IsEnabledAsync(Arg.Any<string>());
    }
}
