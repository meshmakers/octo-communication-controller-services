using System.Diagnostics.CodeAnalysis;
using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Models;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v3;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.PipelineExecutionServiceTests;

[SuppressMessage("Non-substitutable member", "NS1004:Argument matcher used with a non-virtual member of a class.")]
internal class BatchCompleteExecutionAsyncTests : PipelineExecutionServiceTestsBase
{
    [Test]
    public async Task BatchCompleteExecutionsAsync_EmptyList_DoesNothing()
    {
        // Act
        await PipelineExecutionService.BatchCompleteExecutionsAsync(TenantId, []);

        // Assert
        await CommunicationRepository.DidNotReceive().BulkUpdatePipelineExecutionsAsync(
            Arg.Any<string>(),
            Arg.Any<IReadOnlyList<PipelineExecutionUpdate>>());
    }

    [Test]
    public async Task BatchCompleteExecutionsAsync_UnknownTenant_ThrowsException()
    {
        // Arrange
        var dtos = new List<PipelineExecutionEndDto>
        {
            new()
            {
                ExecutionId = Guid.NewGuid().ToString(),
                Status = PipelineExecutionStatus.Completed,
                CompletedAt = DateTime.UtcNow,
                DurationMs = 100
            }
        };

        await Assert.That(async () =>
                await PipelineExecutionService.BatchCompleteExecutionsAsync("unknown", dtos))
            .Throws<PipelineExecutionServiceException>()
            .WithMessageContaining("Tenant not enabled");
    }

    [Test]
    public async Task BatchCompleteExecutionsAsync_MultipleDtos_CallsBulkUpdate()
    {
        // Arrange
        var completedAt = DateTime.UtcNow;
        var dtos = new List<PipelineExecutionEndDto>
        {
            new()
            {
                ExecutionId = Guid.NewGuid().ToString(),
                Status = PipelineExecutionStatus.Completed,
                CompletedAt = completedAt,
                DurationMs = 100
            },
            new()
            {
                ExecutionId = Guid.NewGuid().ToString(),
                Status = PipelineExecutionStatus.Completed,
                CompletedAt = completedAt,
                DurationMs = 200
            }
        };

        CommunicationRepository.BulkUpdatePipelineExecutionsAsync(TenantId, Arg.Any<IReadOnlyList<PipelineExecutionUpdate>>())
            .Returns(2);

        // Act
        await PipelineExecutionService.BatchCompleteExecutionsAsync(TenantId, dtos);

        // Assert
        await CommunicationRepository.Received(1).BulkUpdatePipelineExecutionsAsync(
            TenantId,
            Arg.Is<IReadOnlyList<PipelineExecutionUpdate>>(list =>
                list.Count == 2 &&
                list.All(u => u.Status == RtPipelineExecutionStatusEnum.Completed)));
    }

    [Test]
    public async Task BatchCompleteExecutionsAsync_WithFailures_StoresErrorEvents()
    {
        // Arrange
        var completedAt = DateTime.UtcNow;
        var dtos = new List<PipelineExecutionEndDto>
        {
            new()
            {
                ExecutionId = Guid.NewGuid().ToString(),
                Status = PipelineExecutionStatus.Failed,
                CompletedAt = completedAt,
                DurationMs = 50,
                ErrorMessage = "Test error"
            },
            new()
            {
                ExecutionId = Guid.NewGuid().ToString(),
                Status = PipelineExecutionStatus.Completed,
                CompletedAt = completedAt,
                DurationMs = 100
            }
        };

        CommunicationRepository.BulkUpdatePipelineExecutionsAsync(TenantId, Arg.Any<IReadOnlyList<PipelineExecutionUpdate>>())
            .Returns(2);

        // Act
        await PipelineExecutionService.BatchCompleteExecutionsAsync(TenantId, dtos);

        // Assert - should store error event for the failed execution
        await CommunicationEventService.Received(1).StoreErrorEventAsync(
            TenantId,
            Arg.Is<string>(msg => msg.Contains("Test error")));
    }
}
