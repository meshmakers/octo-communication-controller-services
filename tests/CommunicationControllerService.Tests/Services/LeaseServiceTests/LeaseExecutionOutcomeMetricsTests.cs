using System.Diagnostics.Metrics;
using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using NSubstitute;
using Recorded = Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper.RecordedMeasurement;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.LeaseServiceTests;

/// <summary>
///     AB#4924 × AB#5425 — a leased execution's outcome reaches <c>octo.pipeline.execution.count</c>.
///     A dedicated adapter's outcome is counted in <c>PipelineExecutionService</c>, where the adapter
///     reports it; a leased execution is completed by the lease release instead and used to be
///     missing from the counter altogether.
/// </summary>
/// <remarks>
///     The borrower tenant id is shared with every other lease test, which may release leases
///     concurrently, so the assertions check for the expected outcome being present rather than for
///     an exact count.
/// </remarks>
[NotInParallel(nameof(MeterListener))]
internal class LeaseExecutionOutcomeMetricsTests : LeaseServiceTestsBase
{
    private const string ExecutionId = "7a1d6f1c-6f2f-4f2a-9a43-2a0f0a3a9c22";

    private static List<Recorded> Collect(Func<Task> act) =>
        MeasurementCapture.Collect(PipelineExecutionMetrics.MeterName,
            m => m.Instrument == "octo.pipeline.execution.count"
                 && m.Tags.GetValueOrDefault("octo.tenant.id") == BorrowerTenantId,
            () => act().GetAwaiter().GetResult(), instrumentsOwner: typeof(PipelineExecutionMetrics));

    private async Task<string> GrantWithRunningExecutionAsync()
    {
        // The counter is gated by the per-tenant observability opt-in (AB#5432).
        WorkloadObservabilityOptIn.Refresh(BorrowerTenantId, optedIn: true, TimeSpan.FromMinutes(5));
        ArrangeGrantableLease();
        CommunicationRepository.GetPipelineExecutionAsync(BorrowerTenantId, ExecutionId)
            .Returns(new RtPipelineExecution
            {
                RtId = OctoObjectId.GenerateNewId(),
                CkTypeId = SystemCommunicationCkIds.RtCkPipelineExecutionTypeId,
                ExecutionId = ExecutionId,
                Status = RtPipelineExecutionStatusEnum.Running,
                StartedAt = DateTime.UtcNow.AddSeconds(-5)
            });
        var result = await LeaseService.GrantLeaseAsync(LenderTenantId, AdapterPoolRtId, ARequest(ExecutionId));
        return result.LeaseId!;
    }

    [Test]
    public async Task ASuccessfulRelease_CountsACompletedExecution()
    {
        var leaseId = await GrantWithRunningExecutionAsync();

        var recorded = Collect(() => LeaseService.ReleaseLeaseAsync(ConnectionId, new LeaseResultDto
        {
            LeaseId = leaseId,
            Reason = LeaseReleaseReasonDto.Completed,
            Success = true
        }));

        await Assert.That(recorded.Any(r => r.Tags.GetValueOrDefault("octo.pipeline.outcome") == "completed"))
            .IsTrue();
    }

    [Test]
    public async Task AFailedRelease_CountsAFailedExecution()
    {
        var leaseId = await GrantWithRunningExecutionAsync();

        var recorded = Collect(() => LeaseService.ReleaseLeaseAsync(ConnectionId, new LeaseResultDto
        {
            LeaseId = leaseId,
            Reason = LeaseReleaseReasonDto.Completed,
            Success = false,
            StatusMessage = "boom"
        }));

        await Assert.That(recorded.Any(r => r.Tags.GetValueOrDefault("octo.pipeline.outcome") == "failed"))
            .IsTrue();
    }
}
