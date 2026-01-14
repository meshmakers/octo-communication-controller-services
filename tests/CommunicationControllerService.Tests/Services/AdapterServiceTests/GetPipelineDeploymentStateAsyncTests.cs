using System.Diagnostics.CodeAnalysis;
using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Adapters;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v2;
using Meshmakers.Octo.Runtime.Contracts;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.AdapterServiceTests;

internal class GetPipelineDeploymentStateAsyncTests : AdapterServiceTestsBase
{
    [Test]
    [SuppressMessage("Non-substitutable member", "NS1004:Argument matcher used with a non-virtual member of a class.")]
    public async Task GetPipelineDeploymentStateAsync_TenantNotInCache_ThrowsException()
    {
        // Arrange
        AdapterCache.TryGetTenant("unknownTenant", out Arg.Any<AdapterTenant?>())
            .Returns(false);

        var rtPipeline = RtEntityCreator.CreatePipeline();

        // Act & Assert
        await Assert.That(async () =>
                await AdapterService.GetPipelineDeploymentStateAsync("unknownTenant", rtPipeline.ToRtEntityId()))
            .Throws<AdapterServiceException>()
            .WithMessageContaining("Tenant not enabled");
    }

    [Test]
    public async Task GetPipelineDeploymentStateAsync_PipelineNotFound_ThrowsException()
    {
        // Arrange
        var rtPipeline = RtEntityCreator.CreatePipeline();

        CommunicationRepository.GetPipelineAsync(TenantId, rtPipeline.ToRtEntityId())
            .Returns((RtPipeline?)null);

        // Act & Assert
        var exception = await Assert.That(async () =>
                await AdapterService.GetPipelineDeploymentStateAsync(TenantId, rtPipeline.ToRtEntityId()))
            .Throws<AdapterServiceException>();

         await Assert.That(exception).IsNotNull()
             .And.Member(e => e.Message, msg => msg.Contains("Pipeline").And.Contains("not found"));
    }

    [Test]
    public async Task GetPipelineDeploymentStateAsync_DeployedState_ReturnsSuccess()
    {
        // Arrange
        var rtPipeline = RtEntityCreator.CreatePipeline();
        rtPipeline.DeploymentState = RtDeploymentStateEnum.Deployed;
        rtPipeline.StatusMessage = "Successfully deployed";

        CommunicationRepository.GetPipelineAsync(TenantId, rtPipeline.ToRtEntityId())
            .Returns(rtPipeline);

        // Act
        var result = await AdapterService.GetPipelineDeploymentStateAsync(TenantId, rtPipeline.ToRtEntityId());

        // Assert
        using var _ = Assert.Multiple();

        await Assert.That(result).IsNotNull()
            .And.Member(r => r.PipelineRtEntityId, id => id.IsEqualTo(rtPipeline.ToRtEntityId()));

        await Assert.That(result).IsNotNull()
            .And.Member(r => r.State, state => state.IsEqualTo(DeploymentState.Success));

        await Assert.That(result.StateMessages).IsEqualTo("Successfully deployed");
    }

    [Test]
    public async Task GetPipelineDeploymentStateAsync_PendingState_ReturnsProcessing()
    {
        // Arrange
        var rtPipeline = RtEntityCreator.CreatePipeline();
        rtPipeline.DeploymentState = RtDeploymentStateEnum.Pending;

        CommunicationRepository.GetPipelineAsync(TenantId, rtPipeline.ToRtEntityId())
            .Returns(rtPipeline);

        // Act
        var result = await AdapterService.GetPipelineDeploymentStateAsync(TenantId, rtPipeline.ToRtEntityId());

        // Assert
        await Assert.That(result).IsNotNull()
            .And.Member(r => r.State, state => state.IsEqualTo(DeploymentState.Processing));
    }

    [Test]
    public async Task GetPipelineDeploymentStateAsync_UndeployedState_ReturnsProcessing()
    {
        // Arrange
        var rtPipeline = RtEntityCreator.CreatePipeline();
        rtPipeline.DeploymentState = RtDeploymentStateEnum.Undeployed;

        CommunicationRepository.GetPipelineAsync(TenantId, rtPipeline.ToRtEntityId())
            .Returns(rtPipeline);

        // Act
        var result = await AdapterService.GetPipelineDeploymentStateAsync(TenantId, rtPipeline.ToRtEntityId());

        // Assert
        await Assert.That(result).IsNotNull()
            .And.Member(r => r.State, state => state.IsEqualTo(DeploymentState.Processing));
    }

    [Test]
    public async Task GetPipelineDeploymentStateAsync_ErrorState_ReturnsFailed()
    {
        // Arrange
        var rtPipeline = RtEntityCreator.CreatePipeline();
        rtPipeline.DeploymentState = RtDeploymentStateEnum.Error;
        rtPipeline.StatusMessage = "Deployment failed";

        CommunicationRepository.GetPipelineAsync(TenantId, rtPipeline.ToRtEntityId())
            .Returns(rtPipeline);

        // Act
        var result = await AdapterService.GetPipelineDeploymentStateAsync(TenantId, rtPipeline.ToRtEntityId());

        // Assert
        using var _ = Assert.Multiple();

        await Assert.That(result).IsNotNull()
            .And.Member(r => r.State, state => state.IsEqualTo(DeploymentState.Failed));

        await Assert.That(result.StateMessages).IsEqualTo("Deployment failed");
    }

    [Test]
    public async Task GetPipelineDeploymentStateAsync_UnsupportedState_ThrowsException()
    {
        // Arrange
        var rtPipeline = RtEntityCreator.CreatePipeline();
        rtPipeline.DeploymentState = (RtDeploymentStateEnum)999; // Invalid state

        CommunicationRepository.GetPipelineAsync(TenantId, rtPipeline.ToRtEntityId())
            .Returns(rtPipeline);

        // Act & Assert
        var exception = await Assert.That(async () =>
                await AdapterService.GetPipelineDeploymentStateAsync(TenantId, rtPipeline.ToRtEntityId()))
            .Throws<AdapterServiceException>();

        await Assert.That(exception).IsNotNull()
            .And.Member(e => e.Message, msg => msg.Contains("Deployment state").And.Contains("not supported"));
    }

    [Test]
    public async Task GetPipelineDeploymentStateAsync_NullStatusMessage_ReturnsNull()
    {
        // Arrange
        var rtPipeline = RtEntityCreator.CreatePipeline();
        rtPipeline.DeploymentState = RtDeploymentStateEnum.Deployed;
        rtPipeline.StatusMessage = null;

        CommunicationRepository.GetPipelineAsync(TenantId, rtPipeline.ToRtEntityId())
            .Returns(rtPipeline);

        // Act
        var result = await AdapterService.GetPipelineDeploymentStateAsync(TenantId, rtPipeline.ToRtEntityId());

        // Assert
        await Assert.That(result).IsNotNull();
        await Assert.That(result.StateMessages).IsNull();
    }
}
