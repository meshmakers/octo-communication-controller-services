using System.Diagnostics.CodeAnalysis;
using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v2;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.PipelineExecutionServiceTests;

[SuppressMessage("Non-substitutable member", "NS1004:Argument matcher used with a non-virtual member of a class.")]
internal class MarkInterruptedTests : PipelineExecutionServiceTestsBase
{
    [Test]
    public async Task MarkExecutionsAsInterruptedAsync_UnknownTenant_ReturnsWithoutAction()
    {
        // Arrange
        var rtAdapter = RtEntityCreator.CreateAdapter();

        // Act - Should not throw
        await PipelineExecutionService.MarkExecutionsAsInterruptedAsync("unknown", rtAdapter.ToRtEntityId());

        // Assert - No repository calls should be made for unknown tenant
        await CommunicationRepository.DidNotReceive()
            .GetRunningExecutionsForAdapterAsync(Arg.Any<string>(), Arg.Any<Meshmakers.Octo.ConstructionKit.Contracts.RtEntityId>());
    }

    [Test]
    public async Task MarkExecutionsAsInterruptedAsync_NoRunningExecutions_DoesNothing()
    {
        // Arrange
        var rtAdapter = RtEntityCreator.CreateAdapter();
        CommunicationRepository.GetRunningExecutionsForAdapterAsync(TenantId, rtAdapter.ToRtEntityId())
            .Returns(new List<RtPipelineExecution>());

        // Act
        await PipelineExecutionService.MarkExecutionsAsInterruptedAsync(TenantId, rtAdapter.ToRtEntityId());

        // Assert
        await CommunicationRepository.DidNotReceive().UpdatePipelineExecutionAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<RtPipelineExecutionStatusEnum>(),
            Arg.Any<DateTime?>(),
            Arg.Any<int?>(),
            Arg.Any<string?>());
    }

    [Test]
    public async Task MarkExecutionsAsInterruptedAsync_RunningExecutions_MarksAsInterrupted()
    {
        // Arrange
        var rtAdapter = RtEntityCreator.CreateAdapter();
        var execution1 = RtEntityCreator.CreatePipelineExecution();
        var execution2 = RtEntityCreator.CreatePipelineExecution();

        CommunicationRepository.GetRunningExecutionsForAdapterAsync(TenantId, rtAdapter.ToRtEntityId())
            .Returns(new List<RtPipelineExecution> { execution1, execution2 });

        // Act
        await PipelineExecutionService.MarkExecutionsAsInterruptedAsync(TenantId, rtAdapter.ToRtEntityId());

        // Assert
        await CommunicationRepository.Received(1).UpdatePipelineExecutionAsync(
            TenantId,
            execution1.ExecutionId!,
            RtPipelineExecutionStatusEnum.Interrupted,
            null,
            null,
            "Adapter disconnected");

        await CommunicationRepository.Received(1).UpdatePipelineExecutionAsync(
            TenantId,
            execution2.ExecutionId!,
            RtPipelineExecutionStatusEnum.Interrupted,
            null,
            null,
            "Adapter disconnected");
    }

    [Test]
    public async Task ReportInterruptedExecutionResultAsync_UnknownTenant_ThrowsException()
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
                await PipelineExecutionService.ReportInterruptedExecutionResultAsync("unknown", rtAdapter.ToRtEntityId(), endDto))
            .Throws<PipelineExecutionServiceException>()
            .WithMessageContaining("Tenant not enabled");
    }

    [Test]
    public async Task ReportInterruptedExecutionResultAsync_ExecutionNotFound_ThrowsException()
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

        await Assert.That(async () =>
                await PipelineExecutionService.ReportInterruptedExecutionResultAsync(TenantId, rtAdapter.ToRtEntityId(), endDto))
            .Throws<PipelineExecutionServiceException>()
            .WithMessageContaining("not found");
    }

    [Test]
    public async Task ReportInterruptedExecutionResultAsync_NotInterrupted_ReturnsWithoutUpdate()
    {
        // Arrange
        var rtAdapter = RtEntityCreator.CreateAdapter();
        var executionId = Guid.NewGuid().ToString();
        var execution = RtEntityCreator.CreatePipelineExecution(executionId, RtPipelineExecutionStatusEnum.Running);
        var endDto = new PipelineExecutionEndDto
        {
            ExecutionId = executionId,
            Status = PipelineExecutionStatus.Completed,
            CompletedAt = DateTime.UtcNow,
            DurationMs = 1000
        };

        CommunicationRepository.GetPipelineExecutionAsync(TenantId, executionId)
            .Returns(execution);

        // Act
        await PipelineExecutionService.ReportInterruptedExecutionResultAsync(TenantId, rtAdapter.ToRtEntityId(), endDto);

        // Assert - Should not update
        await CommunicationRepository.DidNotReceive().UpdatePipelineExecutionAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<RtPipelineExecutionStatusEnum>(),
            Arg.Any<DateTime?>(),
            Arg.Any<int?>(),
            Arg.Any<string?>());
    }

    [Test]
    public async Task ReportInterruptedExecutionResultAsync_WasInterrupted_UpdatesWithFinalStatus()
    {
        // Arrange
        var rtAdapter = RtEntityCreator.CreateAdapter();
        var executionId = Guid.NewGuid().ToString();
        var execution = RtEntityCreator.CreatePipelineExecution(executionId, RtPipelineExecutionStatusEnum.Interrupted);
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
        await PipelineExecutionService.ReportInterruptedExecutionResultAsync(TenantId, rtAdapter.ToRtEntityId(), endDto);

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
    public async Task GetInterruptedExecutionIdsAsync_UnknownTenant_ReturnsEmptyList()
    {
        // Arrange
        var rtAdapter = RtEntityCreator.CreateAdapter();

        // Act
        var result = await PipelineExecutionService.GetInterruptedExecutionIdsAsync("unknown", rtAdapter.ToRtEntityId());

        // Assert
        await Assert.That(result).IsEmpty();
    }

    [Test]
    public async Task GetInterruptedExecutionIdsAsync_ValidTenant_ReturnsIds()
    {
        // Arrange
        var rtAdapter = RtEntityCreator.CreateAdapter();
        var expectedIds = new List<string> { "exec1", "exec2", "exec3" };

        CommunicationRepository.GetInterruptedExecutionIdsAsync(TenantId, rtAdapter.ToRtEntityId())
            .Returns(expectedIds);

        // Act
        var result = await PipelineExecutionService.GetInterruptedExecutionIdsAsync(TenantId, rtAdapter.ToRtEntityId());

        // Assert
        await Assert.That(result).Count().IsEqualTo(3);
        await Assert.That(result).Contains("exec1");
        await Assert.That(result).Contains("exec2");
        await Assert.That(result).Contains("exec3");
    }
}
