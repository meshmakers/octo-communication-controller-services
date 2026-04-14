using System.Diagnostics.CodeAnalysis;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v3;
using Meshmakers.Octo.Communication.Contracts.MessageObjects;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Commands;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.TriggerManagementServiceTests;

internal abstract class TriggerManagementServiceTestsBase
{
    protected const string TenantId = "tenantId";
    protected readonly TriggerManagementService TriggerManagementService;
    protected readonly ICommunicationRepository CommunicationRepository;
    protected readonly ICommandClient<RemoveRecurringJobsByScheduleGroupRequest> RemoveRecurringJobsCommandClient;
    protected readonly IRoutedCommandClient<ExecutePipelineRequest> ExecuteMeshPipelineCommandClient;
    protected readonly IDistributionEventHubService DistributionEventHubService;
    protected readonly ICommunicationEventService CommunicationEventService;

    [SuppressMessage("Substitute creation", "NS2002:Constructor parameters count mismatch.")]
    protected TriggerManagementServiceTestsBase()
    {
        CommunicationRepository = Substitute.For<ICommunicationRepository>();
        RemoveRecurringJobsCommandClient =
            Substitute.For<ICommandClient<RemoveRecurringJobsByScheduleGroupRequest>>();
        ExecuteMeshPipelineCommandClient =
            Substitute.For<IRoutedCommandClient<ExecutePipelineRequest>>();
        DistributionEventHubService = Substitute.For<IDistributionEventHubService>();
        CommunicationEventService = Substitute.For<ICommunicationEventService>();

        var logger = Substitute.For<ILogger<TriggerManagementService>>();

        TriggerManagementService = new TriggerManagementService(
            logger,
            CommunicationRepository,
            RemoveRecurringJobsCommandClient,
            ExecuteMeshPipelineCommandClient,
            DistributionEventHubService,
            CommunicationEventService);

        // Default: RemoveScheduleAsync returns empty triggers and succeeds
        CommunicationRepository.GetTriggersAsync(TenantId)
            .Returns(Array.Empty<RtPipelineTrigger>());
        RemoveRecurringJobsCommandClient
            .GetResponse<GenericCommandResponse>(Arg.Any<RemoveRecurringJobsByScheduleGroupRequest>(),
                Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>())
            .Returns(new GenericCommandResponse());
    }
}
