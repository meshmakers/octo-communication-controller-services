using Meshmakers.Octo.Communication.Contracts.MessageObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using NSubstitute;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.TriggerManagementServiceTests;

/// <summary>
/// Pins the M4-B.2 wire contract: <c>StartExecutePipelineAsync</c>'s new
/// <c>isDryRun</c> parameter must populate
/// <see cref="ExecutePipelineRequest.IsDryRun"/> on the routed-command message the
/// adapter consumes. A regression here means the controller acknowledges the dry-run
/// intent but the adapter executes for real — the exact failure mode the flag exists
/// to prevent.
/// </summary>
internal class StartExecutePipelineAsyncTests : TriggerManagementServiceTestsBase
{
    [Test]
    public async Task StartExecutePipelineAsync_DryRunTrue_SetsIsDryRunOnRequest()
    {
        var pipelineRtId = OctoObjectId.GenerateNewId();
        var executionId = Guid.NewGuid();
        var startedAt = DateTime.UtcNow;
        ExecutePipelineRequest? captured = null;

        ExecuteMeshPipelineCommandClient
            .GetResponse<ExecutePipelineResponse>(Arg.Any<string>(), Arg.Any<ExecutePipelineRequest>(),
                Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>())
            .Returns(callInfo =>
            {
                captured = callInfo.ArgAt<ExecutePipelineRequest>(1);
                return new ExecutePipelineResponse(true, null, executionId, startedAt);
            });

        var result = await TriggerManagementService.StartExecutePipelineAsync(TenantId, pipelineRtId,
            pipelineInput: null, isDryRun: true);

        await Assert.That(result).IsNotNull();
        await Assert.That(captured).IsNotNull();
        await Assert.That(captured!.IsDryRun).IsTrue();
        await Assert.That(captured.TenantId).IsEqualTo(TenantId);
    }

    [Test]
    public async Task StartExecutePipelineAsync_DryRunFalseByDefault_PreservesClassicSemantics()
    {
        var pipelineRtId = OctoObjectId.GenerateNewId();
        var executionId = Guid.NewGuid();
        ExecutePipelineRequest? captured = null;

        ExecuteMeshPipelineCommandClient
            .GetResponse<ExecutePipelineResponse>(Arg.Any<string>(), Arg.Any<ExecutePipelineRequest>(),
                Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>())
            .Returns(callInfo =>
            {
                captured = callInfo.ArgAt<ExecutePipelineRequest>(1);
                return new ExecutePipelineResponse(true, null, executionId, DateTime.UtcNow);
            });

        // Call the 3-arg overload (no isDryRun) — backward-compat guard.
        await TriggerManagementService.StartExecutePipelineAsync(TenantId, pipelineRtId, pipelineInput: null);

        await Assert.That(captured).IsNotNull();
        await Assert.That(captured!.IsDryRun).IsFalse();
    }
}
