using System.Diagnostics.CodeAnalysis;
using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Adapters;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Contracts.Hubs;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v2;
using Meshmakers.Octo.Runtime.Contracts;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.AdapterServiceTests;

internal class UpdateConfigurationStateAsyncTests : AdapterServiceTestsBase
{
    [Test]
    [SuppressMessage("Non-substitutable member", "NS1004:Argument matcher used with a non-virtual member of a class.")]
    public async Task UpdateConfigurationStateAsync_TenantNotInCache_ThrowsException()
    {
        // Arrange
        AdapterCache.TryGetTenant("unknownTenant", out Arg.Any<AdapterTenant?>())
            .Returns(false);

        var rtAdapter = RtEntityCreator.CreateAdapter();
        var deploymentResult = new DeploymentResult { IsSuccess = true, ErrorMessages = null };

        // Act & Assert
        var exception = await Assert.That(async () =>
                await AdapterService.UpdateConfigurationStateAsync("unknownTenant", rtAdapter.ToRtEntityId(), deploymentResult))
            .Throws<AdapterServiceException>();

        await Assert.That(exception).IsNotNull()
            .And.Member(e => e.Message, msg => msg.Contains("Adapter").And.Contains("not loaded"));
    }

    [Test]
    public async Task UpdateConfigurationStateAsync_AdapterNotInCache_ThrowsException()
    {
        // Arrange
        var rtAdapter = RtEntityCreator.CreateAdapter();
        var deploymentResult = new DeploymentResult { IsSuccess = true, ErrorMessages = null };

        // Act & Assert
        var exception = await Assert.That(async () =>
                await AdapterService.UpdateConfigurationStateAsync(TenantId, rtAdapter.ToRtEntityId(), deploymentResult))
            .Throws<AdapterServiceException>();

        await Assert.That(exception).IsNotNull()
            .And.Member(e => e.Message, msg => msg.Contains("Adapter").And.Contains("not loaded"));
    }

    [Test]
    public async Task UpdateConfigurationStateAsync_SuccessfulDeployment_SetsConfiguredStateForAllPipelines()
    {
        // Arrange
        var rtAdapter = RtEntityCreator.CreateAdapter();
        var rtDataPipeline = RtEntityCreator.CreateDataPipeline();
        var rtPipeline1 = RtEntityCreator.CreatePipeline();
        var rtPipeline2 = RtEntityCreator.CreatePipeline();

        AdapterTenant.AddAdapter(rtAdapter.ToRtEntityId(), ConnectionId, new AdapterConfigurationDto(
            rtAdapter.ToRtEntityId(),
            null,
            [
                new PipelineConfigurationDto(rtDataPipeline.RtId, rtPipeline1.ToRtEntityId(), false,
                    rtPipeline1.PipelineDefinition, []),
                new PipelineConfigurationDto(rtDataPipeline.RtId, rtPipeline2.ToRtEntityId(), false,
                    rtPipeline2.PipelineDefinition, [])
            ]
        ));

        var deploymentResult = new DeploymentResult { IsSuccess = true, ErrorMessages = null };

        // Act
        await AdapterService.UpdateConfigurationStateAsync(TenantId, rtAdapter.ToRtEntityId(), deploymentResult);

        // Assert
        using var _ = Assert.Multiple();

        await CommunicationRepository.Received(1)
            .SetAdapterConfigurationStateAsync(TenantId, rtAdapter.ToRtEntityId(), RtConfigurationStateEnum.Configured, null);

        await CommunicationRepository.Received(1)
            .SetPipelineDeploymentStateAsync(TenantId, rtPipeline1.ToRtEntityId(), RtDeploymentStateEnum.Deployed, null);

        await CommunicationRepository.Received(1)
            .SetPipelineDeploymentStateAsync(TenantId, rtPipeline2.ToRtEntityId(), RtDeploymentStateEnum.Deployed, null);
    }

    [Test]
    public async Task UpdateConfigurationStateAsync_FailedDeploymentWithoutErrors_SetsErrorStateForAdapter()
    {
        // Arrange
        var rtAdapter = RtEntityCreator.CreateAdapter();
        var rtDataPipeline = RtEntityCreator.CreateDataPipeline();
        var rtPipeline = RtEntityCreator.CreatePipeline();

        AdapterTenant.AddAdapter(rtAdapter.ToRtEntityId(), ConnectionId, new AdapterConfigurationDto(
            rtAdapter.ToRtEntityId(),
            null,
            [
                new PipelineConfigurationDto(rtDataPipeline.RtId, rtPipeline.ToRtEntityId(), false,
                    rtPipeline.PipelineDefinition, [])
            ]
        ));

        var deploymentResult = new DeploymentResult { IsSuccess = false, ErrorMessages = null };

        // Act
        await AdapterService.UpdateConfigurationStateAsync(TenantId, rtAdapter.ToRtEntityId(), deploymentResult);

        // Assert
        using var _ = Assert.Multiple();

        await CommunicationRepository.Received(1)
            .SetAdapterConfigurationStateAsync(TenantId, rtAdapter.ToRtEntityId(), RtConfigurationStateEnum.Error,
                Arg.Any<string>());

        // Pipeline without specific error should be marked as Deployed
        await CommunicationRepository.Received(1)
            .SetPipelineDeploymentStateAsync(TenantId, rtPipeline.ToRtEntityId(), RtDeploymentStateEnum.Deployed, null);
    }

    [Test]
    public async Task UpdateConfigurationStateAsync_FailedDeploymentWithPipelineErrors_SetsErrorStateForAffectedPipelines()
    {
        // Arrange
        var rtAdapter = RtEntityCreator.CreateAdapter();
        var rtDataPipeline = RtEntityCreator.CreateDataPipeline();
        var rtPipeline1 = RtEntityCreator.CreatePipeline();
        var rtPipeline2 = RtEntityCreator.CreatePipeline();

        AdapterTenant.AddAdapter(rtAdapter.ToRtEntityId(), ConnectionId, new AdapterConfigurationDto(
            rtAdapter.ToRtEntityId(),
            null,
            [
                new PipelineConfigurationDto(rtDataPipeline.RtId, rtPipeline1.ToRtEntityId(), false,
                    rtPipeline1.PipelineDefinition, []),
                new PipelineConfigurationDto(rtDataPipeline.RtId, rtPipeline2.ToRtEntityId(), false,
                    rtPipeline2.PipelineDefinition, [])
            ]
        ));

        var errorMessages = new List<DeploymentUpdateErrorMessageDto>
        {
            new()
            {
                ErrorCategory = DeploymentErrorCategories.Uncategorized,
                PipelineRtEntityId = rtPipeline1.ToRtEntityId(),
                ErrorMessage = "Pipeline 1 failed to deploy"
            }
        };

        var deploymentResult = new DeploymentResult { IsSuccess = false, ErrorMessages = errorMessages };

        // Act
        await AdapterService.UpdateConfigurationStateAsync(TenantId, rtAdapter.ToRtEntityId(), deploymentResult);

        // Assert
        using var _ = Assert.Multiple();

        await CommunicationRepository.Received(1)
            .SetAdapterConfigurationStateAsync(TenantId, rtAdapter.ToRtEntityId(), RtConfigurationStateEnum.Error,
                Arg.Is<string>(msg => msg.Contains("Pipeline 1 failed to deploy")));

        // Pipeline with error should be marked as Error
        await CommunicationRepository.Received(1)
            .SetPipelineDeploymentStateAsync(TenantId, rtPipeline1.ToRtEntityId(), RtDeploymentStateEnum.Error,
                "Pipeline 1 failed to deploy");

        // Pipeline without error should be marked as Deployed
        await CommunicationRepository.Received(1)
            .SetPipelineDeploymentStateAsync(TenantId, rtPipeline2.ToRtEntityId(), RtDeploymentStateEnum.Deployed, null);
    }

    [Test]
    public async Task UpdateConfigurationStateAsync_FailedDeploymentWithMultiplePipelineErrors_SetsErrorStateCorrectly()
    {
        // Arrange
        var rtAdapter = RtEntityCreator.CreateAdapter();
        var rtDataPipeline = RtEntityCreator.CreateDataPipeline();
        var rtPipeline1 = RtEntityCreator.CreatePipeline();
        var rtPipeline2 = RtEntityCreator.CreatePipeline();

        AdapterTenant.AddAdapter(rtAdapter.ToRtEntityId(), ConnectionId, new AdapterConfigurationDto(
            rtAdapter.ToRtEntityId(),
            null,
            [
                new PipelineConfigurationDto(rtDataPipeline.RtId, rtPipeline1.ToRtEntityId(), false,
                    rtPipeline1.PipelineDefinition, []),
                new PipelineConfigurationDto(rtDataPipeline.RtId, rtPipeline2.ToRtEntityId(), false,
                    rtPipeline2.PipelineDefinition, [])
            ]
        ));

        var errorMessages = new List<DeploymentUpdateErrorMessageDto>
        {
            new()
            {
                ErrorCategory = DeploymentErrorCategories.Uncategorized,
                PipelineRtEntityId = rtPipeline1.ToRtEntityId(),
                ErrorMessage = "Error 1"
            },
            new()
            {
                ErrorCategory = DeploymentErrorCategories.Uncategorized,
                PipelineRtEntityId = rtPipeline2.ToRtEntityId(),
                ErrorMessage = "Error 2"
            }
        };

        var deploymentResult = new DeploymentResult { IsSuccess = false, ErrorMessages = errorMessages };

        // Act
        await AdapterService.UpdateConfigurationStateAsync(TenantId, rtAdapter.ToRtEntityId(), deploymentResult);

        // Assert
        using var _ = Assert.Multiple();

        await CommunicationRepository.Received(1)
            .SetPipelineDeploymentStateAsync(TenantId, rtPipeline1.ToRtEntityId(), RtDeploymentStateEnum.Error, "Error 1");

        await CommunicationRepository.Received(1)
            .SetPipelineDeploymentStateAsync(TenantId, rtPipeline2.ToRtEntityId(), RtDeploymentStateEnum.Error, "Error 2");
    }
}
