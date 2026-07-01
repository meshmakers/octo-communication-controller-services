using System.Diagnostics.CodeAnalysis;
using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;
using Meshmakers.Octo.ConstructionKit.Contracts;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.PipelineExecutionServiceTests;

/// <summary>
/// Unit tests for the fresh-startup orphan resolution at the service layer (AB#4280): a restarted
/// adapter process asks the controller to fail its executions that predate the process start.
/// </summary>
[SuppressMessage("Non-substitutable member", "NS1004:Argument matcher used with a non-virtual member of a class.")]
internal class FailOrphanedExecutionsTests : PipelineExecutionServiceTestsBase
{
    [Test]
    public async Task FailOrphanedExecutionsForAdapterAsync_UnknownTenant_ReturnsZeroWithoutRepositoryCall()
    {
        // Arrange
        var rtAdapter = RtEntityCreator.CreateAdapter();

        // Act
        var count = await PipelineExecutionService.FailOrphanedExecutionsForAdapterAsync(
            "unknown", rtAdapter.ToRtEntityId(), DateTime.UtcNow);

        // Assert
        await Assert.That(count).IsEqualTo(0);
        await CommunicationRepository.DidNotReceive()
            .FailOrphanedExecutionsForAdapterAsync(Arg.Any<string>(), Arg.Any<RtEntityId>(), Arg.Any<DateTime>());
    }

    [Test]
    public async Task FailOrphanedExecutionsForAdapterAsync_NoOrphans_WritesNoEvent()
    {
        // Arrange
        var rtAdapter = RtEntityCreator.CreateAdapter();
        var processStart = DateTime.UtcNow;
        CommunicationRepository
            .FailOrphanedExecutionsForAdapterAsync(TenantId, rtAdapter.ToRtEntityId(), processStart)
            .Returns(0);

        // Act
        var count = await PipelineExecutionService.FailOrphanedExecutionsForAdapterAsync(
            TenantId, rtAdapter.ToRtEntityId(), processStart);

        // Assert
        await Assert.That(count).IsEqualTo(0);
        await CommunicationEventService.DidNotReceive()
            .StoreInformationEventAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<RtEntityId>());
    }

    [Test]
    public async Task FailOrphanedExecutionsForAdapterAsync_Orphans_DelegatesReturnsCountAndWritesEvent()
    {
        // Arrange
        var rtAdapter = RtEntityCreator.CreateAdapter();
        var processStart = DateTime.UtcNow;
        CommunicationRepository
            .FailOrphanedExecutionsForAdapterAsync(TenantId, rtAdapter.ToRtEntityId(), processStart)
            .Returns(2);

        // Act
        var count = await PipelineExecutionService.FailOrphanedExecutionsForAdapterAsync(
            TenantId, rtAdapter.ToRtEntityId(), processStart);

        // Assert
        await Assert.That(count).IsEqualTo(2);
        await CommunicationRepository.Received(1)
            .FailOrphanedExecutionsForAdapterAsync(TenantId, rtAdapter.ToRtEntityId(), processStart);
        await CommunicationEventService.Received(1)
            .StoreInformationEventAsync(TenantId, Arg.Is<string>(m => m.Contains("2") && m.Contains("orphaned")),
                rtAdapter.ToRtEntityId());
    }
}
