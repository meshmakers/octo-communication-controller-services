using System.Diagnostics.CodeAnalysis;
using Meshmakers.Octo.ConstructionKit.Contracts;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.PipelineExecutionServiceTests;

/// <summary>
/// Unit tests for the connection-aware stuck-execution reaper at the service layer (AB#4280).
/// The connection-aware filtering itself (Running on an offline vs. online adapter) is exercised by
/// the repository integration tests; here we pin the service contract: grace-cutoff computation,
/// delegation, count propagation and audit-event emission.
/// </summary>
[SuppressMessage("Non-substitutable member", "NS1004:Argument matcher used with a non-virtual member of a class.")]
internal class FailStuckExecutionsTests : PipelineExecutionServiceTestsBase
{
    [Test]
    public async Task FailStuckExecutionsAsync_ComputesGraceCutoffAndDelegates()
    {
        // Arrange
        const int graceMinutes = 15;
        var before = DateTime.UtcNow.AddMinutes(-graceMinutes);
        CommunicationRepository.FailStuckExecutionsAsync(TenantId, Arg.Any<DateTime>()).Returns(0);

        // Act
        await PipelineExecutionService.FailStuckExecutionsAsync(TenantId, graceMinutes);
        var after = DateTime.UtcNow.AddMinutes(-graceMinutes);

        // Assert - the cutoff passed to the repository is "now - grace"
        await CommunicationRepository.Received(1).FailStuckExecutionsAsync(TenantId,
            Arg.Is<DateTime>(cutoff => cutoff >= before && cutoff <= after));
    }

    [Test]
    public async Task FailStuckExecutionsAsync_NothingStuck_WritesNoEvent()
    {
        // Arrange
        CommunicationRepository.FailStuckExecutionsAsync(TenantId, Arg.Any<DateTime>()).Returns(0);

        // Act
        var count = await PipelineExecutionService.FailStuckExecutionsAsync(TenantId, 15);

        // Assert
        await Assert.That(count).IsEqualTo(0);
        await CommunicationEventService.DidNotReceive()
            .StoreInformationEventAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    [Test]
    public async Task FailStuckExecutionsAsync_SomeStuck_ReturnsCountAndWritesEvent()
    {
        // Arrange
        CommunicationRepository.FailStuckExecutionsAsync(TenantId, Arg.Any<DateTime>()).Returns(3);

        // Act
        var count = await PipelineExecutionService.FailStuckExecutionsAsync(TenantId, 15);

        // Assert
        await Assert.That(count).IsEqualTo(3);
        await CommunicationEventService.Received(1)
            .StoreInformationEventAsync(TenantId, Arg.Is<string>(m => m.Contains("3") && m.Contains("stuck")));
    }
}
