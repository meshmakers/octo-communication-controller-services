using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Commands;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

internal static class NotificationEngine
{
    public static readonly OctoObjectId DataFlowRtId = new("670000000000000000002580");

    public static readonly OctoObjectId PipelineRtId = new("670000000000000000002581");

    public static string CreateScheduleAddress(string tenantId) =>
        $"queue:{QueueNames.NotificationScheduleChannelName.ToLower()}-{tenantId.ToLower()}-{PipelineRtId.ToString().ToLower()}";
}
