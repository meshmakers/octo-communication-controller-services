using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Models;

/// <summary>
///     Outcome of a successful single-pipeline move on the repository layer.
///     Carries the before/after adapter ids as full <c>RtEntityId</c>s so the
///     controller can re-deploy onto the target adapter (which needs both
///     <c>OctoObjectId</c> and <c>CkTypeId</c>) without an extra fetch.
/// </summary>
public sealed record PipelineMoveResult(
    OctoObjectId PipelineRtId,
    RtEntityId OldAdapterRtEntityId,
    RtEntityId NewAdapterRtEntityId);
