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
        CommunicationRepository.FailStuckExecutionsAsync(TenantId, Arg.Any<DateTime>(), Arg.Any<DateTime>()).Returns(0);

        // Act
        await PipelineExecutionService.FailStuckExecutionsAsync(TenantId, graceMinutes);
        var after = DateTime.UtcNow.AddMinutes(-graceMinutes);

        // Assert - the cutoff passed to the repository is "now - grace"
        await CommunicationRepository.Received(1).FailStuckExecutionsAsync(TenantId,
            Arg.Is<DateTime>(cutoff => cutoff >= before && cutoff <= after), Arg.Any<DateTime>());
    }

    /// <summary>
    ///     🔴 AB#5326: the leased cutoff is the lease TTL BEHIND the ordinary one, and it is derived
    ///     from the same option the scheduler grants leases with. A leased execution younger than
    ///     that is still watched by the lease and must not be judged by its adapter's connection
    ///     state — which, for a leased adapter, can never say Online.
    /// </summary>
    [Test]
    public async Task FailStuckExecutionsAsync_LeasedCutoffTrailsTheOrdinaryOneByTheLeaseTtl()
    {
        // Arrange
        const int graceMinutes = 15;
        ControllerOptions.LeaseTtlMinutes = 42;
        CommunicationRepository.FailStuckExecutionsAsync(TenantId, Arg.Any<DateTime>(), Arg.Any<DateTime>())
            .Returns(0);
        DateTime capturedGrace = default;
        DateTime capturedLeased = default;
        await CommunicationRepository.FailStuckExecutionsAsync(TenantId, Arg.Do<DateTime>(v => capturedGrace = v),
            Arg.Do<DateTime>(v => capturedLeased = v));

        // Act
        await PipelineExecutionService.FailStuckExecutionsAsync(TenantId, graceMinutes);

        // Assert
        var distance = capturedGrace - capturedLeased;
        await Assert.That(distance.TotalMinutes).IsBetween(41.9, 42.1);
    }

    [Test]
    public async Task FailStuckExecutionsAsync_NothingStuck_WritesNoEvent()
    {
        // Arrange
        CommunicationRepository.FailStuckExecutionsAsync(TenantId, Arg.Any<DateTime>(), Arg.Any<DateTime>()).Returns(0);

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
        CommunicationRepository.FailStuckExecutionsAsync(TenantId, Arg.Any<DateTime>(), Arg.Any<DateTime>()).Returns(3);

        // Act
        var count = await PipelineExecutionService.FailStuckExecutionsAsync(TenantId, 15);

        // Assert
        await Assert.That(count).IsEqualTo(3);
        await CommunicationEventService.Received(1)
            .StoreInformationEventAsync(TenantId, Arg.Is<string>(m => m.Contains("3") && m.Contains("stuck")));
    }
}
