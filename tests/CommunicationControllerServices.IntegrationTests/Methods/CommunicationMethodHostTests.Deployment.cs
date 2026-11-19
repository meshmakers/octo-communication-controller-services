using System.Text.Json;
using FluentAssertions;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Methods.Service;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Backend.CommunicationControllerServices.TenantApi.v1.Controllers;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects.ApiErrors;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.IntegrationTests.Methods;

/// <summary>
///     AB#9050: the DeploymentSite, DeployableWorkload (deploy / undeploy), AdapterPool.Scale and PipelineTrigger methods. Each
///     is called once through REST (the controller action, its own code on the facade's direct path) and once as a method on
///     the same substituted services: the service call, its arguments and the answer must be the same.
/// </summary>
public sealed partial class CommunicationMethodHostTests
{
    [Fact]
    public async Task DeploymentSiteDeploy_CallsTheSameServiceAsRest()
    {
        var site = OctoObjectId.GenerateNewId();

        var rest = await DeploymentSiteController().DeployDeploymentSiteAsync(site);
        var method = await InvokeAsync(SystemCommunicationCkIds.DeploymentSiteDeployMethodId, site.ToString());

        rest.Should().BeOfType<NoContentResult>();
        method.Success.Should().BeTrue();
        await _deploymentSites.Received(2).DeployDeploymentSiteAsync(TenantId, site);
    }

    [Fact]
    public async Task DeploymentSiteDeploy_ARefusalOfTheService_IsTheDeclaredErrorWithTheRestText()
    {
        var site = OctoObjectId.GenerateNewId();
        _deploymentSites.DeployDeploymentSiteAsync(TenantId, site)
            .Throws(DeploymentSiteServiceException.EdgePoolNotDeployable(TenantId, site, "edge-1"));

        var rest = await DeploymentSiteController().DeployDeploymentSiteAsync(site);
        var method = await InvokeAsync(SystemCommunicationCkIds.DeploymentSiteDeployMethodId, site.ToString());

        method.Error!.Code.Should().Be("DEPLOYMENT_SITE_DEPLOY_FAILED");
        method.Error.Parameters["message"].Should().Be(((ErrorResponse)((BadRequestObjectResult)rest).Value!).ErrorMessage);
    }

    [Fact]
    public async Task DeploymentSiteUndeploy_CallsTheSameServiceAsRest_AlsoWhileCommunicationIsDisabled()
    {
        var site = OctoObjectId.GenerateNewId();
        _communicationConfiguration.IsEnabledAsync(TenantId).Returns(false);

        var rest = await DeploymentSiteController().UndeployDeploymentSiteAsync(site);
        var method = await InvokeAsync(SystemCommunicationCkIds.DeploymentSiteUndeployMethodId, site.ToString());

        rest.Should().BeOfType<NoContentResult>();
        method.Success.Should().BeTrue("undeploy stays open so remediation always works (AB#4255)");
        await _deploymentSites.Received(2).UndeployDeploymentSiteAsync(TenantId, site);

        _deploymentSites.UndeployDeploymentSiteAsync(TenantId, site).Throws(
            DeploymentSiteServiceException.PoolAlreadyNotDeployed(TenantId, site, "cloud-1",
                RtDeploymentStateEnum.Undeployed));
        var failedRest = await DeploymentSiteController().UndeployDeploymentSiteAsync(site);
        var failedMethod = await InvokeAsync(SystemCommunicationCkIds.DeploymentSiteUndeployMethodId, site.ToString());

        failedMethod.Error!.Code.Should().Be("DEPLOYMENT_SITE_UNDEPLOY_FAILED");
        failedMethod.Error.Parameters["message"].Should()
            .Be(((ErrorResponse)((BadRequestObjectResult)failedRest).Value!).ErrorMessage);
    }

    public static TheoryData<string> GatedMethods() => new()
    {
        SystemCommunicationCkIds.DeploymentSiteDeployMethodId,
        SystemCommunicationCkIds.DeployableWorkloadDeployMethodId,
        SystemCommunicationCkIds.AdapterPoolScaleMethodId
    };

    /// <summary>AB#4255: the resource-creating operations refuse on a tenant whose Communication is disabled, like REST (409).</summary>
    [Theory]
    [MemberData(nameof(GatedMethods))]
    public async Task AResourceCreatingMethod_IsRefusedWhileCommunicationIsDisabled_LikeRest(string methodId)
    {
        var target = OctoObjectId.GenerateNewId();
        _communicationConfiguration.IsEnabledAsync(TenantId).Returns(false);

        var rest = methodId switch
        {
            SystemCommunicationCkIds.DeploymentSiteDeployMethodId => await DeploymentSiteController()
                .DeployDeploymentSiteAsync(target),
            SystemCommunicationCkIds.DeployableWorkloadDeployMethodId => await DeploymentSiteController()
                .DeployWorkloadAsync(target),
            _ => await DeploymentSiteController().ScaleAdapterPoolAsync(target, 2)
        };
        var method = await InvokeAsync(methodId, target.ToString(), ValidParameters(methodId));

        var conflict = rest.Should().BeOfType<ConflictObjectResult>().Subject;
        method.Error!.Code.Should().Be("COMMUNICATION_DISABLED");
        method.Error.Parameters["message"].Should().Be(((OperationFailedErrorDto)conflict.Value!).Message);
        _deploymentSites.ReceivedCalls().Should().BeEmpty("nothing is deployed behind the tenant delete guard's back");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("ValuesYaml overrides resources.limits.cpu")]
    public async Task WorkloadDeploy_AnswersWhatRestAnswers_TheWarningIsTheResult(string? warning)
    {
        var workload = OctoObjectId.GenerateNewId();
        _deploymentSites.DeployWorkloadAsync(TenantId, workload).Returns(warning);

        var rest = await DeploymentSiteController().DeployWorkloadAsync(workload);
        var method = await InvokeAsync(SystemCommunicationCkIds.DeployableWorkloadDeployMethodId, workload.ToString(),
            ckTypeId: "System.Communication/Application");

        method.Success.Should().BeTrue();
        await _deploymentSites.Received(2).DeployWorkloadAsync(TenantId, workload);
        using var result = JsonDocument.Parse(method.ResultJson!);
        if (warning == null)
        {
            rest.Should().BeOfType<NoContentResult>();
            (result.RootElement.TryGetProperty("warning", out var none) && none.ValueKind != JsonValueKind.Null)
                .Should().BeFalse();
        }
        else
        {
            var dto = ((OkObjectResult)rest).Value.Should().BeOfType<DeployWorkloadResultDto>().Subject;
            SameMembers(dto, method.ResultJson!);
        }
    }

    [Fact]
    public async Task WorkloadUndeploy_CallsTheSameServiceAsRest_AndMapsARefusal()
    {
        var workload = OctoObjectId.GenerateNewId();
        _communicationConfiguration.IsEnabledAsync(TenantId).Returns(false);

        var rest = await DeploymentSiteController().UndeployWorkloadAsync(workload);
        var method = await InvokeAsync(SystemCommunicationCkIds.DeployableWorkloadUndeployMethodId, workload.ToString());

        rest.Should().BeOfType<NoContentResult>();
        method.Success.Should().BeTrue("undeploy stays open while Communication is disabled");
        await _deploymentSites.Received(2).UndeployWorkloadAsync(TenantId, workload);

        _deploymentSites.UndeployWorkloadAsync(TenantId, workload)
            .Throws(DeploymentSiteServiceException.WorkloadNotFound(TenantId, workload));
        var failedRest = await DeploymentSiteController().UndeployWorkloadAsync(workload);
        var failedMethod = await InvokeAsync(SystemCommunicationCkIds.DeployableWorkloadUndeployMethodId,
            workload.ToString());

        failedMethod.Error!.Code.Should().Be("WORKLOAD_UNDEPLOY_FAILED");
        failedMethod.Error.Parameters["message"].Should()
            .Be(((ErrorResponse)((BadRequestObjectResult)failedRest).Value!).ErrorMessage);
    }

    [Fact]
    public async Task AdapterPoolScale_AnswersWhatRestAnswers_TheEffectiveCount()
    {
        var pool = OctoObjectId.GenerateNewId();
        _deploymentSites.ScaleAdapterPoolAsync(TenantId, pool, 9).Returns(4);

        var rest = await DeploymentSiteController().ScaleAdapterPoolAsync(pool, 9);
        var method = await InvokeAsync(SystemCommunicationCkIds.AdapterPoolScaleMethodId, pool.ToString(),
            new { replicas = 9 });

        var dto = ((OkObjectResult)rest).Value.Should().BeOfType<AdapterPoolScaleResultDto>().Subject;
        dto.Replicas.Should().Be(4);
        method.Success.Should().BeTrue();
        SameMembers(dto, method.ResultJson!);
        await _deploymentSites.Received(2).ScaleAdapterPoolAsync(TenantId, pool, 9);
    }

    [Fact]
    public async Task AdapterPoolScale_ANegativeCount_IsRefusedBeforeTheService_OnBothPaths()
    {
        var pool = OctoObjectId.GenerateNewId();

        var rest = await DeploymentSiteController().ScaleAdapterPoolAsync(pool, -1);
        var method = await InvokeAsync(SystemCommunicationCkIds.AdapterPoolScaleMethodId, pool.ToString(),
            new { replicas = -1 });

        ((ErrorResponse)((BadRequestObjectResult)rest).Value!).ErrorMessage.Should()
            .Be(TenantApi.v1.Controllers.DeploymentSiteController.NegativeReplicasMessage);
        method.Error!.Code.Should().Be(CkMethodErrorCodes.MethodValidationFailed);
        method.Error.Parameters[CkMethodErrorParameters.Field].Should().Be("replicas");
        _deploymentSites.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task PipelineTriggerDeployAllAndUndeployAll_CallTheSameServicesAsRest_ForTheCallsTenant()
    {
        var restDeploy = await PipelineTriggerController().DeployTrigger();
        var restUndeploy = await PipelineTriggerController().UndeployTrigger();
        var deploy = await InvokeAsync(SystemCommunicationCkIds.PipelineTriggerDeployAllMethodId, targetRtId: null);
        var undeploy = await InvokeAsync(SystemCommunicationCkIds.PipelineTriggerUndeployAllMethodId, targetRtId: null);

        restDeploy.Should().BeOfType<NoContentResult>();
        restUndeploy.Should().BeOfType<NoContentResult>();
        deploy.Success.Should().BeTrue();
        undeploy.Success.Should().BeTrue();
        await _trigger.Received(2).UpdateScheduleAsync(TenantId);
        await _trigger.Received(2).RemoveScheduleAsync(TenantId);
    }

    [Fact]
    public async Task PipelineTriggerDeployAll_AControllerFailure_CarriesItsText()
    {
        var failure = TriggerManagementServiceException.UpdateScheduleFailed(TenantId,
            new InvalidOperationException(InternalDetail));
        _trigger.UpdateScheduleAsync(TenantId).Throws(failure);

        var rest = await PipelineTriggerController().DeployTrigger();
        var method = await InvokeAsync(SystemCommunicationCkIds.PipelineTriggerDeployAllMethodId, targetRtId: null);

        method.Error!.Code.Should().Be("PIPELINE_TRIGGERS_DEPLOY_FAILED");
        method.Error.Parameters["message"].Should().Be(((ErrorResponse)((BadRequestObjectResult)rest).Value!).ErrorMessage,
            "the controller's own exception carries the text REST answered");
        JsonSerializer.Serialize(method.Error).Should().NotContain("internal detail", "never the inner exception's text");
    }

    /// <summary>
    ///     An exception that is not the service's own refusal surfaces as <c>METHOD_HANDLER_FAILED</c> (REST answered it 500): its
    ///     text never reaches the caller.
    /// </summary>
    [Fact]
    public async Task ADeploymentMethod_AnUnexpectedException_IsHandlerFailed_WithoutItsText()
    {
        var workload = OctoObjectId.GenerateNewId();
        _deploymentSites.DeployWorkloadAsync(TenantId, workload).Throws(new InvalidOperationException(InternalDetail));

        var method = await InvokeAsync(SystemCommunicationCkIds.DeployableWorkloadDeployMethodId, workload.ToString());

        method.Error!.Code.Should().Be(CkMethodErrorCodes.MethodHandlerFailed);
        JsonSerializer.Serialize(method.Error).Should().NotContain("internal detail");
    }

    /// <summary>
    ///     AB#9048 rule: a handler opens the caller scope, so a service that judges "the caller" (identity lookups during the
    ///     provisioning of an adapter's service account) sees the validated caller, never the system path.
    /// </summary>
    [Fact]
    public async Task WorkloadDeploy_TheServiceSeesTheValidatedCaller()
    {
        var workload = OctoObjectId.GenerateNewId();
        CommunicationMethodCaller? seen = null;
        _deploymentSites.DeployWorkloadAsync(TenantId, workload).Returns(_ =>
        {
            seen = CommunicationMethodCaller.Current;
            return Task.FromResult<string?>(null);
        });

        var method = await InvokeAsync(SystemCommunicationCkIds.DeployableWorkloadDeployMethodId, workload.ToString(),
            token: Token(roles: [CommunicationManagement, UserManagement]));

        method.Success.Should().BeTrue();
        seen.Should().NotBeNull();
        seen!.User.FindFirst("sub")!.Value.Should().Be(Subject);
        seen.User.HasRole(UserManagement).Should().BeTrue();
        seen.AccessToken.Should().NotBeNullOrEmpty();
    }

    private DeploymentSiteController DeploymentSiteController()
    {
        var controller = new DeploymentSiteController(NullLogger<DeploymentSiteController>.Instance, _deploymentSites,
            _communicationConfiguration);
        controller.ControllerContext = new ControllerContext { HttpContext = HttpContextOf(null) };
        return controller;
    }

    private PipelineTriggerController PipelineTriggerController()
    {
        var controller = new PipelineTriggerController(NullLogger<PipelineTriggerController>.Instance, _trigger);
        controller.ControllerContext = new ControllerContext { HttpContext = HttpContextOf(null) };
        return controller;
    }
}
