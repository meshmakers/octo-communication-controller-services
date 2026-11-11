using System.Reflection;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;
using Meshmakers.Octo.Common.DistributionEventHub;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using Meshmakers.Octo.Communication.Contracts.MessageObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
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

    /// <summary>
    /// AB#7460: the marker given by the method invoker lands on the published request unchanged; every other caller
    /// leaves the parameter at its default and the request carries none.
    /// </summary>
    [Test]
    public async Task StartExecutePipelineAsync_MethodInvocation_IsSetOnTheDirectRequest()
    {
        var pipelineRtId = OctoObjectId.GenerateNewId();
        var marker = new ExecutePipelineMethodInvocation
        {
            MethodId = "Acme.Assets/Device.UpdateFirmware-1",
            InvocationId = Guid.NewGuid(),
            CkTypeId = "Acme.Assets/Device",
            PipelineRtId = pipelineRtId.ToString()
        };
        ExecutePipelineRequest? captured = null;
        ExecuteMeshPipelineCommandClient
            .GetResponse<ExecutePipelineResponse>(Arg.Any<string>(), Arg.Any<ExecutePipelineRequest>(),
                Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>())
            .Returns(callInfo =>
            {
                captured = callInfo.ArgAt<ExecutePipelineRequest>(1);
                return new ExecutePipelineResponse(true, null, Guid.NewGuid(), DateTime.UtcNow);
            });

        await TriggerManagementService.StartExecutePipelineAsync(TenantId, pipelineRtId, pipelineInput: null,
            methodInvocation: marker);

        await Assert.That(captured!.MethodInvocation).IsEqualTo(marker);
    }

    [Test]
    public async Task StartExecutePipelineAsync_WithoutMethodInvocation_RequestCarriesNone()
    {
        var pipelineRtId = OctoObjectId.GenerateNewId();
        ExecutePipelineRequest? captured = null;
        ExecuteMeshPipelineCommandClient
            .GetResponse<ExecutePipelineResponse>(Arg.Any<string>(), Arg.Any<ExecutePipelineRequest>(),
                Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>())
            .Returns(callInfo =>
            {
                captured = callInfo.ArgAt<ExecutePipelineRequest>(1);
                return new ExecutePipelineResponse(true, null, Guid.NewGuid(), DateTime.UtcNow);
            });

        await TriggerManagementService.StartExecutePipelineAsync(TenantId, pipelineRtId, pipelineInput: null);

        await Assert.That(captured!.MethodInvocation).IsNull();
    }

    /// <summary>
    /// AB#4918 wake gate: the execute-pipeline queue is non-durable, so the gate must have
    /// completed before the command is sent — otherwise the message is silently dropped
    /// while the adapter is scaled to 0.
    /// </summary>
    [Test]
    public async Task StartExecutePipelineAsync_InvokesWakeGateBeforeSendingCommand()
    {
        var pipelineRtId = OctoObjectId.GenerateNewId();

        ExecuteMeshPipelineCommandClient
            .GetResponse<ExecutePipelineResponse>(Arg.Any<string>(), Arg.Any<ExecutePipelineRequest>(),
                Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>())
            .Returns(new ExecutePipelineResponse(true, null, Guid.NewGuid(), DateTime.UtcNow));

        await TriggerManagementService.StartExecutePipelineAsync(TenantId, pipelineRtId, pipelineInput: null);

        Received.InOrder(() =>
        {
            WorkloadLifecycleService.EnsureWorkloadRunningForPipelineAsync(TenantId,
                Arg.Is<OctoObjectId>(id => id.ToString() == pipelineRtId.ToString()));
            ExecuteMeshPipelineCommandClient.GetResponse<ExecutePipelineResponse>(Arg.Any<string>(),
                Arg.Any<ExecutePipelineRequest>(), Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>());
        });
    }

    /// <summary>
    /// The gate call sits OUTSIDE the try block: a wake failure must surface as the typed
    /// <see cref="WorkloadLifecycleServiceException"/> (unwrapped, actionable "retry shortly"
    /// message) and the execute command must never be sent.
    /// </summary>
    [Test]
    public async Task StartExecutePipelineAsync_WakeGateThrows_PropagatesUnwrappedAndDoesNotSend()
    {
        var pipelineRtId = OctoObjectId.GenerateNewId();

        WorkloadLifecycleService
            .EnsureWorkloadRunningForPipelineAsync(TenantId, Arg.Any<OctoObjectId>())
            .ThrowsAsync(WorkloadLifecycleServiceException.WakeTimedOut(TenantId, pipelineRtId,
                "meshtest-adapter", TimeSpan.FromSeconds(1)));

        await Assert.That(async () =>
                await TriggerManagementService.StartExecutePipelineAsync(TenantId, pipelineRtId,
                    pipelineInput: null))
            .Throws<WorkloadLifecycleServiceException>();

        await ExecuteMeshPipelineCommandClient.DidNotReceiveWithAnyArgs()
            .GetResponse<ExecutePipelineResponse>(Arg.Any<string>(), Arg.Any<ExecutePipelineRequest>(),
                Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>());
    }

    /// <summary>
    ///     AB#8716: no answer to the execute command means the adapter is offline (or has not deployed the pipeline). The
    ///     failure names the adapter, so the caller of Pipeline.Execute learns why without reading the controller log.
    /// </summary>
    [Test]
    public async Task StartExecutePipelineAsync_NoAnswerFromTheAdapter_NamesTheAdapterAsOffline()
    {
        var pipelineRtId = OctoObjectId.GenerateNewId();
        var adapter = RtEntityCreator.CreateAdapter();
        adapter.Name = "offline-adapter-7";
        CommunicationRepository.GetAdapterByPipelineAsync(TenantId, Arg.Is<RtEntityId>(id => id.RtId == pipelineRtId))
            .Returns(adapter);
        ExecuteMeshPipelineCommandClient
            .GetResponse<ExecutePipelineResponse>(Arg.Any<string>(), Arg.Any<ExecutePipelineRequest>(),
                Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>())
            .ThrowsAsync(Timeout());

        var exception = await Assert.That(async () =>
                await TriggerManagementService.StartExecutePipelineAsync(TenantId, pipelineRtId, pipelineInput: null))
            .Throws<TriggerManagementServiceException>();

        await Assert.That(exception!.Message).Contains("Adapter 'offline-adapter-7' did not answer");
        await Assert.That(exception.Message).Contains("offline");
        await Assert.That(exception.Message).Contains("outcome of the execution is unknown");
        await CommunicationEventService.Received(1).StoreErrorEventAsync(TenantId,
            Arg.Is<string>(m => m.Contains("offline-adapter-7")));
    }

    [Test]
    public async Task StartExecutePipelineAsync_NoAnswer_AndTheAdapterCannotBeRead_StillSaysTheAdapterDidNotAnswer()
    {
        var pipelineRtId = OctoObjectId.GenerateNewId();
        CommunicationRepository.GetAdapterByPipelineAsync(TenantId, Arg.Any<RtEntityId>())
            .ThrowsAsync(new InvalidOperationException("db down"));
        ExecuteMeshPipelineCommandClient
            .GetResponse<ExecutePipelineResponse>(Arg.Any<string>(), Arg.Any<ExecutePipelineRequest>(),
                Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>())
            .ThrowsAsync(Timeout());

        var exception = await Assert.That(async () =>
                await TriggerManagementService.StartExecutePipelineAsync(TenantId, pipelineRtId, pipelineInput: null))
            .Throws<TriggerManagementServiceException>();

        await Assert.That(exception!.Message).Contains("Adapter 'unknown' did not answer");
    }

    /// <summary>The timeout of the routed command client (its constructors are not public).</summary>
    private static DistributionTimeoutException Timeout() =>
        (DistributionTimeoutException)Activator.CreateInstance(typeof(DistributionTimeoutException),
            BindingFlags.NonPublic | BindingFlags.Instance, null,
            ["Command ExecutePipelineRequest timed out after 30 seconds.", new TimeoutException()], null)!;
}
