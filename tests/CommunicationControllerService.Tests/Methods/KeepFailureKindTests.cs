using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Methods;

/// <summary>AB#8037: which failure kind a later report of an execution writes (a stored OutcomeUnknown is never downgraded).</summary>
internal class KeepFailureKindTests
{
    private static RtPipelineExecution Stored(RtPipelineExecutionStatusEnum status,
        RtPipelineExecutionFailureKindEnum? kind) =>
        new() { Status = status, FailureKind = kind };

    [Test]
    [Arguments(null)]
    [Arguments(RtPipelineExecutionFailureKindEnum.Failed)]
    public async Task AStoredOutcomeUnknownSurvivesALaterFailedReport(RtPipelineExecutionFailureKindEnum? reported)
    {
        var kind = CommunicationRepository.KeepFailureKind(RtPipelineExecutionStatusEnum.Failed, reported,
            Stored(RtPipelineExecutionStatusEnum.Failed, RtPipelineExecutionFailureKindEnum.OutcomeUnknown));

        await Assert.That(kind).IsEqualTo(RtPipelineExecutionFailureKindEnum.OutcomeUnknown);
    }

    [Test]
    public async Task AReportedOutcomeUnknownUpgradesAStoredFailed()
    {
        var kind = CommunicationRepository.KeepFailureKind(RtPipelineExecutionStatusEnum.Failed,
            RtPipelineExecutionFailureKindEnum.OutcomeUnknown,
            Stored(RtPipelineExecutionStatusEnum.Failed, RtPipelineExecutionFailureKindEnum.Failed));

        await Assert.That(kind).IsEqualTo(RtPipelineExecutionFailureKindEnum.OutcomeUnknown);
    }

    [Test]
    public async Task AReportWithoutAKindKeepsAStoredFailedKind()
    {
        var kind = CommunicationRepository.KeepFailureKind(RtPipelineExecutionStatusEnum.Failed, null,
            Stored(RtPipelineExecutionStatusEnum.Failed, RtPipelineExecutionFailureKindEnum.Failed));

        await Assert.That(kind).IsEqualTo(RtPipelineExecutionFailureKindEnum.Failed);
    }

    [Test]
    public async Task ANonFailedStatusCarriesNoKind()
    {
        var kind = CommunicationRepository.KeepFailureKind(RtPipelineExecutionStatusEnum.Completed, null,
            Stored(RtPipelineExecutionStatusEnum.Failed, RtPipelineExecutionFailureKindEnum.OutcomeUnknown));

        await Assert.That(kind).IsNull();
    }

    [Test]
    public async Task AStoredKindOfAnExecutionThatWasNotFailedIsIgnored()
    {
        var kind = CommunicationRepository.KeepFailureKind(RtPipelineExecutionStatusEnum.Failed, null,
            Stored(RtPipelineExecutionStatusEnum.Running, RtPipelineExecutionFailureKindEnum.OutcomeUnknown));

        await Assert.That(kind).IsNull();
    }
}
