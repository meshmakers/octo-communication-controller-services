using System.Diagnostics.CodeAnalysis;
using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v2;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.PipelineExecutionServiceTests;

[SuppressMessage("Non-substitutable member", "NS1004:Argument matcher used with a non-virtual member of a class.")]
internal class CompleteExecutionAsyncTests : PipelineExecutionServiceTestsBase
{
    [Test]
    public async Task CompleteExecutionAsync_UnknownTenant_ThrowsException()
    {
        // Arrange
        var rtAdapter = RtEntityCreator.CreateAdapter();
        var endDto = new PipelineExecutionEndDto
        {
            ExecutionId = Guid.NewGuid().ToString(),
            Status = PipelineExecutionStatus.Completed,
            CompletedAt = DateTime.UtcNow,
            DurationMs = 1000
        };

        await Assert.That(async () =>
                await PipelineExecutionService.CompleteExecutionAsync("unknown", rtAdapter.ToRtEntityId(), endDto))
            .Throws<PipelineExecutionServiceException>()
            .WithMessageContaining("Tenant not enabled");
    }

    [Test]
    public async Task CompleteExecutionAsync_ExecutionNotFound_ReturnsGracefully()
    {
        // Arrange
        var rtAdapter = RtEntityCreator.CreateAdapter();
        var executionId = Guid.NewGuid().ToString();
        var endDto = new PipelineExecutionEndDto
        {
            ExecutionId = executionId,
            Status = PipelineExecutionStatus.Completed,
            CompletedAt = DateTime.UtcNow,
            DurationMs = 1000
        };

        CommunicationRepository.GetPipelineExecutionAsync(TenantId, executionId)
            .Returns((RtPipelineExecution?)null);

        // Act - Should not throw, just return gracefully
        await PipelineExecutionService.CompleteExecutionAsync(TenantId, rtAdapter.ToRtEntityId(), endDto);

        // Assert - No update should be made
        await CommunicationRepository.DidNotReceive().UpdatePipelineExecutionAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<RtPipelineExecutionStatusEnum>(),
            Arg.Any<DateTime?>(),
            Arg.Any<int?>(),
            Arg.Any<string?>());
    }

    [Test]
    public async Task CompleteExecutionAsync_Completed_UpdatesRecord()
    {
        // Arrange
        var rtAdapter = RtEntityCreator.CreateAdapter();
        var executionId = Guid.NewGuid().ToString();
        var execution = RtEntityCreator.CreatePipelineExecution(executionId);
        var completedAt = DateTime.UtcNow;
        var endDto = new PipelineExecutionEndDto
        {
            ExecutionId = executionId,
            Status = PipelineExecutionStatus.Completed,
            CompletedAt = completedAt,
            DurationMs = 1000
        };

        CommunicationRepository.GetPipelineExecutionAsync(TenantId, executionId)
            .Returns(execution);

        // Act
        await PipelineExecutionService.CompleteExecutionAsync(TenantId, rtAdapter.ToRtEntityId(), endDto);

        // Assert
        await CommunicationRepository.Received(1).UpdatePipelineExecutionAsync(
            TenantId,
            executionId,
            RtPipelineExecutionStatusEnum.Completed,
            completedAt,
            1000,
            null);
    }

    [Test]
    public async Task CompleteExecutionAsync_Failed_StoresErrorEvent()
    {
        // Arrange
        var rtAdapter = RtEntityCreator.CreateAdapter();
        var executionId = Guid.NewGuid().ToString();
        var execution = RtEntityCreator.CreatePipelineExecution(executionId);
        var errorMessage = "Pipeline execution failed due to timeout";
        var endDto = new PipelineExecutionEndDto
        {
            ExecutionId = executionId,
            Status = PipelineExecutionStatus.Failed,
            CompletedAt = DateTime.UtcNow,
            DurationMs = 5000,
            ErrorMessage = errorMessage
        };

        CommunicationRepository.GetPipelineExecutionAsync(TenantId, executionId)
            .Returns(execution);

        // Act
        await PipelineExecutionService.CompleteExecutionAsync(TenantId, rtAdapter.ToRtEntityId(), endDto);

        // Assert
        await CommunicationRepository.Received(1).UpdatePipelineExecutionAsync(
            TenantId,
            executionId,
            RtPipelineExecutionStatusEnum.Failed,
            Arg.Any<DateTime?>(),
            5000,
            errorMessage);

        await CommunicationEventService.Received(1).StoreErrorEventAsync(
            TenantId,
            Arg.Is<string>(s => s.Contains("failed") && s.Contains(errorMessage)));
    }

    [Test]
    public async Task CompleteExecutionAsync_Cancelled_StoresInformationEvent()
    {
        // Arrange
        var rtAdapter = RtEntityCreator.CreateAdapter();
        var executionId = Guid.NewGuid().ToString();
        var execution = RtEntityCreator.CreatePipelineExecution(executionId);
        var endDto = new PipelineExecutionEndDto
        {
            ExecutionId = executionId,
            Status = PipelineExecutionStatus.Cancelled,
            CompletedAt = DateTime.UtcNow,
            DurationMs = 500
        };

        CommunicationRepository.GetPipelineExecutionAsync(TenantId, executionId)
            .Returns(execution);

        // Act
        await PipelineExecutionService.CompleteExecutionAsync(TenantId, rtAdapter.ToRtEntityId(), endDto);

        // Assert
        await CommunicationEventService.Received(1).StoreInformationEventAsync(
            TenantId,
            Arg.Is<string>(s => s.Contains("cancelled")));
    }

    [Test]
    public async Task CompleteExecutionAsync_RepositoryThrows_WrapsException()
    {
        // Arrange
        var rtAdapter = RtEntityCreator.CreateAdapter();
        var executionId = Guid.NewGuid().ToString();
        var execution = RtEntityCreator.CreatePipelineExecution(executionId);
        var endDto = new PipelineExecutionEndDto
        {
            ExecutionId = executionId,
            Status = PipelineExecutionStatus.Completed,
            CompletedAt = DateTime.UtcNow,
            DurationMs = 1000
        };

        CommunicationRepository.GetPipelineExecutionAsync(TenantId, executionId)
            .Returns(execution);
        CommunicationRepository.UpdatePipelineExecutionAsync(
            TenantId,
            executionId,
            Arg.Any<RtPipelineExecutionStatusEnum>(),
            Arg.Any<DateTime?>(),
            Arg.Any<int?>(),
            Arg.Any<string?>())
            .Returns(Task.FromException(new InvalidOperationException("Database error")));

        await Assert.That(async () =>
                await PipelineExecutionService.CompleteExecutionAsync(TenantId, rtAdapter.ToRtEntityId(), endDto))
            .Throws<PipelineExecutionServiceException>()
            .WithMessageContaining("Failed to complete execution");
    }
}
