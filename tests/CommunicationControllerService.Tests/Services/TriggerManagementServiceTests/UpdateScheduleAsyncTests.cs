using System.Diagnostics.CodeAnalysis;
using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;
using Meshmakers.Octo.Common.DistributionEventHub;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v3;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Messages;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.TriggerManagementServiceTests;

[SuppressMessage("Non-substitutable member", "NS1004:Argument matcher used with a non-virtual member of a class.")]
[SuppressMessage("Usage", "NS5000:Unused received check.")]
internal class UpdateScheduleAsyncTests : TriggerManagementServiceTestsBase
{
    [Test]
    public async Task UpdateScheduleAsync_TriggerWithNoPipelines_SetsErrorStateAndStoresErrorEvent()
    {
        // Arrange
        var trigger = RtEntityCreator.CreatePipelineTrigger(cronExpression: "0 * * * *");
        var triggersAndPipelines =
            new Dictionary<RtPipelineTrigger, IList<RtPipeline>>
            {
                { trigger, new List<RtPipeline>() }
            };

        CommunicationRepository.GetTriggersAndPipelinesAsync(TenantId)
            .Returns(triggersAndPipelines);

        // Act
        await TriggerManagementService.UpdateScheduleAsync(TenantId);

        // Assert
        await CommunicationRepository.Received(1).SetPipelineTriggerDeploymentStateAsync(
            TenantId, trigger.RtId, RtDeploymentStateEnum.Error);

        await DistributionEventHubService.DidNotReceive()
            .ScheduleRecurringSendAsync(Arg.Any<PipelineTriggerSchedule>(), Arg.Any<string>(),
                Arg.Any<RecurringSchedulingOptions>());

        await CommunicationEventService.Received(1).StoreErrorEventAsync(TenantId,
            Arg.Is<string>(s => s.Contains(trigger.RtId.ToString()!) && s.Contains("no associated pipelines")));
    }

    [Test]
    public async Task UpdateScheduleAsync_TriggerWithPipeline_SchedulesAndSetsDeployedState()
    {
        // Arrange
        var trigger = RtEntityCreator.CreatePipelineTrigger(cronExpression: "0 * * * *");
        var pipeline = RtEntityCreator.CreatePipeline();
        var triggersAndPipelines =
            new Dictionary<RtPipelineTrigger, IList<RtPipeline>>
            {
                { trigger, new List<RtPipeline> { pipeline } }
            };

        CommunicationRepository.GetTriggersAndPipelinesAsync(TenantId)
            .Returns(triggersAndPipelines);

        // Act
        await TriggerManagementService.UpdateScheduleAsync(TenantId);

        // Assert
        await DistributionEventHubService.Received(1)
            .ScheduleRecurringSendAsync(Arg.Any<PipelineTriggerSchedule>(), Arg.Any<string>(),
                Arg.Any<RecurringSchedulingOptions>());

        await CommunicationRepository.Received(1).SetPipelineTriggerDeploymentStateAsync(
            TenantId, trigger.RtId, RtDeploymentStateEnum.Deployed);
    }

    [Test]
    public async Task UpdateScheduleAsync_TriggerWithMultiplePipelines_SchedulesAllPipelines()
    {
        // Arrange
        var trigger = RtEntityCreator.CreatePipelineTrigger(cronExpression: "0 * * * *");
        var pipeline1 = RtEntityCreator.CreatePipeline();
        var pipeline2 = RtEntityCreator.CreatePipeline();
        var triggersAndPipelines =
            new Dictionary<RtPipelineTrigger, IList<RtPipeline>>
            {
                { trigger, new List<RtPipeline> { pipeline1, pipeline2 } }
            };

        CommunicationRepository.GetTriggersAndPipelinesAsync(TenantId)
            .Returns(triggersAndPipelines);

        // Act
        await TriggerManagementService.UpdateScheduleAsync(TenantId);

        // Assert
        await DistributionEventHubService.Received(2)
            .ScheduleRecurringSendAsync(Arg.Any<PipelineTriggerSchedule>(), Arg.Any<string>(),
                Arg.Any<RecurringSchedulingOptions>());

        await CommunicationRepository.Received(1).SetPipelineTriggerDeploymentStateAsync(
            TenantId, trigger.RtId, RtDeploymentStateEnum.Deployed);
    }

    [Test]
    public async Task UpdateScheduleAsync_MixedTriggers_HandlesEachCorrectly()
    {
        // Arrange
        var triggerWithPipeline = RtEntityCreator.CreatePipelineTrigger(name: "Has Pipeline");
        var triggerWithoutPipeline = RtEntityCreator.CreatePipelineTrigger(name: "No Pipeline");
        var pipeline = RtEntityCreator.CreatePipeline();
        var triggersAndPipelines =
            new Dictionary<RtPipelineTrigger, IList<RtPipeline>>
            {
                { triggerWithPipeline, new List<RtPipeline> { pipeline } },
                { triggerWithoutPipeline, new List<RtPipeline>() }
            };

        CommunicationRepository.GetTriggersAndPipelinesAsync(TenantId)
            .Returns(triggersAndPipelines);

        // Act
        await TriggerManagementService.UpdateScheduleAsync(TenantId);

        // Assert
        await CommunicationRepository.Received(1).SetPipelineTriggerDeploymentStateAsync(
            TenantId, triggerWithPipeline.RtId, RtDeploymentStateEnum.Deployed);

        await CommunicationRepository.Received(1).SetPipelineTriggerDeploymentStateAsync(
            TenantId, triggerWithoutPipeline.RtId, RtDeploymentStateEnum.Error);

        // Only one schedule call for the trigger with a pipeline
        await DistributionEventHubService.Received(1)
            .ScheduleRecurringSendAsync(Arg.Any<PipelineTriggerSchedule>(), Arg.Any<string>(),
                Arg.Any<RecurringSchedulingOptions>());
    }

    [Test]
    public async Task UpdateScheduleAsync_NoTriggers_StoresInformationEventWithZeroCount()
    {
        // Arrange
        CommunicationRepository.GetTriggersAndPipelinesAsync(TenantId)
            .Returns(new Dictionary<RtPipelineTrigger, IList<RtPipeline>>());

        // Act
        await TriggerManagementService.UpdateScheduleAsync(TenantId);

        // Assert
        await CommunicationEventService.Received(1).StoreInformationEventAsync(TenantId,
            Arg.Is<string>(s => s.Contains("0 trigger(s)")));
    }
}
