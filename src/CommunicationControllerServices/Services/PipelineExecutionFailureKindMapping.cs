using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

/// <summary>
///     Maps the failure kind an adapter or pool member reports (AB#8037) onto the CK enum stored on
///     <c>PipelineExecution.FailureKind</c>.
/// </summary>
internal static class PipelineExecutionFailureKindMapping
{
    /// <summary>
    ///     The kind to store: only for a <see cref="RtPipelineExecutionStatusEnum.Failed" /> execution, null otherwise (and
    ///     when the reporter sent none - an adapter built before AB#8037). An unknown value is stored as
    ///     <see cref="RtPipelineExecutionFailureKindEnum.Failed" />: a newer adapter's kind this controller does not know must
    ///     never read as "outcome unknown".
    /// </summary>
    public static RtPipelineExecutionFailureKindEnum? ToCk(RtPipelineExecutionStatusEnum status,
        PipelineExecutionFailureKind? failureKind)
    {
        if (status != RtPipelineExecutionStatusEnum.Failed || failureKind is null)
        {
            return null;
        }

        return failureKind == PipelineExecutionFailureKind.OutcomeUnknown
            ? RtPipelineExecutionFailureKindEnum.OutcomeUnknown
            : RtPipelineExecutionFailureKindEnum.Failed;
    }
}
