using System.Diagnostics.CodeAnalysis;
using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Adapters;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.AdapterServiceTests;

/// <summary>
/// AB#6631 — a blueprint update rewrites a pipeline entity in place. The adapter registers the route of a
/// <c>FromHttpRequest</c> trigger (with its <c>requiredRoles</c> / <c>allowAnonymous</c>) when the pipeline is
/// pushed, so the live route kept the authorization of the OLD definition until the data flow was redeployed
/// by hand. <see cref="AdapterService.RefreshDeployedPipelinesAsync" /> pushes the changed definitions of the
/// pipelines an adapter already runs (AB#6631), drops the ones that are gone and adds the ones a blueprint seeded as
/// Deployed that the adapter does not run yet (AB#6632).
/// </summary>
internal class RefreshDeployedPipelinesAsyncTests : AdapterServiceTestsBase
{
    private const string OldDefinition = "triggers: [{type: FromHttpRequest@2, requiredRoles: []}]";
    private const string NewDefinition = "triggers: [{type: FromHttpRequest@2, requiredRoles: [EnergyCommunityManagement]}]";

    /// <summary>
    /// Registers the adapter with what the controller last pushed (the persisted state at that time) and then
    /// lets the repository answer with <paramref name="persistedPipelines" />, i.e. the state after the update.
    /// </summary>
    private async Task<RtAdapter> ArrangeAdapterRunningAsync(string connectionId, RtDataFlow rtDataFlow,
        RtPipeline runningPipeline, string? adapterConfiguration = "adapter-json",
        params RtPipeline[] persistedPipelines)
    {
        var rtAdapter = RtEntityCreator.CreateAdapter();
        rtAdapter.Configuration = adapterConfiguration;

        // What the controller pushed earlier: built by the service itself from the persisted state then.
        InitAdapterConfiguration(rtAdapter, rtDataFlow, [runningPipeline]);
        var pushed = await AdapterService.GetAdapterConfigurationAsync(TenantId, rtAdapter.ToRtEntityId(), true, true);
        AdapterTenant.AddAdapter(rtAdapter.ToRtEntityId(), connectionId, pushed);

        // The state after the blueprint update.
        InitAdapterConfiguration(rtAdapter, rtDataFlow, persistedPipelines.ToList());
        return rtAdapter;
    }

    [Test]
    [SuppressMessage("Non-substitutable member", "NS1004:Argument matcher used with a non-virtual member of a class.")]
    public async Task ChangedDefinition_IsPushedToTheAdapter_AndTheCacheFollows()
    {
        var rtDataFlow = RtEntityCreator.CreateDataFlow();
        var running = RtEntityCreator.CreatePipeline(OldDefinition);
        var rtAdapter = await ArrangeAdapterRunningAsync(ConnectionId, rtDataFlow, running, "adapter-json",
            RtEntityCreator.CreatePipelineFrom(running, NewDefinition));

        var refreshed = await AdapterService.RefreshDeployedPipelinesAsync(TenantId);

        await Assert.That(refreshed).IsEqualTo(1);
        await AdapterHubCallbacks.Received(1).AdapterConfigurationUpdatedAsync(TenantId,
            Arg.Is<AdapterConfigurationDto>(c =>
                c.AdapterRtEntityId == rtAdapter.ToRtEntityId() &&
                c.Pipelines.Count == 1 &&
                c.Pipelines.Single().NodeConfiguration == NewDefinition));
        await Assert.That(AdapterTenant.AdapterById[rtAdapter.ToRtEntityId()].Configuration.Pipelines.Single()
            .NodeConfiguration).IsEqualTo(NewDefinition);
    }

    [Test]
    [SuppressMessage("Non-substitutable member", "NS1004:Argument matcher used with a non-virtual member of a class.")]
    public async Task UnchangedDefinition_PushesNothing()
    {
        var rtDataFlow = RtEntityCreator.CreateDataFlow();
        var running = RtEntityCreator.CreatePipeline(OldDefinition);
        await ArrangeAdapterRunningAsync(ConnectionId, rtDataFlow, running, "adapter-json", running);

        var refreshed = await AdapterService.RefreshDeployedPipelinesAsync(TenantId);

        await Assert.That(refreshed).IsEqualTo(0);
        await AdapterHubCallbacks.DidNotReceiveWithAnyArgs().AdapterConfigurationUpdatedAsync(default!, default!);
    }

    [Test]
    [SuppressMessage("Non-substitutable member", "NS1004:Argument matcher used with a non-virtual member of a class.")]
    public async Task AdapterConfigurationString_IsNotPushed()
    {
        // An update resets the adapter configuration to the seed (credentials of the EDA adapter); the operator
        // restores it afterwards. Refreshing the pipelines must not hand the live adapter the reset values.
        var rtDataFlow = RtEntityCreator.CreateDataFlow();
        var running = RtEntityCreator.CreatePipeline(OldDefinition);
        var rtAdapter = await ArrangeAdapterRunningAsync(ConnectionId, rtDataFlow, running, "adapter-json",
            RtEntityCreator.CreatePipelineFrom(running, NewDefinition));
        rtAdapter.Configuration = "reset-by-the-update";

        await AdapterService.RefreshDeployedPipelinesAsync(TenantId);

        await AdapterHubCallbacks.Received(1).AdapterConfigurationUpdatedAsync(TenantId,
            Arg.Is<AdapterConfigurationDto>(c => c.AdapterConfiguration == "adapter-json"));
    }

    [Test]
    [SuppressMessage("Non-substitutable member", "NS1004:Argument matcher used with a non-virtual member of a class.")]
    public async Task PipelinePersistedAsDeployedButNotRunning_IsPushedToTheAdapter()
    {
        // AB#6632: a blueprint seeds pipelines as Deployed after the adapter registered; the adapter never
        // received them, so their routes answered 404 until DeployDataFlow was run by hand.
        var rtDataFlow = RtEntityCreator.CreateDataFlow();
        var running = RtEntityCreator.CreatePipeline(OldDefinition);
        var seeded = RtEntityCreator.CreatePipeline("triggers: [{type: FromHttpRequest@2}]");
        var rtAdapter = await ArrangeAdapterRunningAsync(ConnectionId, rtDataFlow, running, "adapter-json",
            RtEntityCreator.CreatePipelineFrom(running), seeded);

        var refreshed = await AdapterService.RefreshDeployedPipelinesAsync(TenantId);

        await Assert.That(refreshed).IsEqualTo(1);
        await AdapterHubCallbacks.Received(1).AdapterConfigurationUpdatedAsync(TenantId,
            Arg.Is<AdapterConfigurationDto>(c =>
                c.Pipelines.Count == 2 &&
                c.Pipelines.Any(p => p.PipelineRtEntityId == running.ToRtEntityId()) &&
                c.Pipelines.Any(p => p.PipelineRtEntityId == seeded.ToRtEntityId())));
        await Assert.That(AdapterTenant.AdapterById[rtAdapter.ToRtEntityId()].Configuration.Pipelines.Count)
            .IsEqualTo(2);
    }

    [Test]
    [SuppressMessage("Non-substitutable member", "NS1004:Argument matcher used with a non-virtual member of a class.")]
    public async Task AdapterRunningNothing_GetsTheSeededPipelines()
    {
        // The fresh-install case: the adapter registered with an empty configuration, then the blueprint
        // seeded its pipelines.
        var rtDataFlow = RtEntityCreator.CreateDataFlow();
        var rtAdapter = RtEntityCreator.CreateAdapter();
        rtAdapter.Configuration = "adapter-json";
        InitAdapterConfiguration(rtAdapter, rtDataFlow, []);
        var pushed = await AdapterService.GetAdapterConfigurationAsync(TenantId, rtAdapter.ToRtEntityId(), true, true);
        AdapterTenant.AddAdapter(rtAdapter.ToRtEntityId(), ConnectionId, pushed);
        var seeded = RtEntityCreator.CreatePipeline(NewDefinition);
        InitAdapterConfiguration(rtAdapter, rtDataFlow, [seeded]);

        var refreshed = await AdapterService.RefreshDeployedPipelinesAsync(TenantId);

        await Assert.That(refreshed).IsEqualTo(1);
        await AdapterHubCallbacks.Received(1).AdapterConfigurationUpdatedAsync(TenantId,
            Arg.Is<AdapterConfigurationDto>(c =>
                c.Pipelines.Count == 1 && c.Pipelines.Single().PipelineRtEntityId == seeded.ToRtEntityId()));
    }

    [Test]
    [SuppressMessage("Non-substitutable member", "NS1004:Argument matcher used with a non-virtual member of a class.")]
    public async Task PipelinePersistedAsUndeployed_IsNotPushed()
    {
        // Only Deployed/Pending pipelines belong on the adapter; an explicitly undeployed one stays off.
        var rtDataFlow = RtEntityCreator.CreateDataFlow();
        var running = RtEntityCreator.CreatePipeline(OldDefinition);
        var undeployed = RtEntityCreator.CreatePipeline("triggers: [{type: FromHttpRequest@2}]");
        undeployed.DeploymentState = RtDeploymentStateEnum.Undeployed;
        await ArrangeAdapterRunningAsync(ConnectionId, rtDataFlow, running, "adapter-json",
            RtEntityCreator.CreatePipelineFrom(running), undeployed);

        var refreshed = await AdapterService.RefreshDeployedPipelinesAsync(TenantId);

        await Assert.That(refreshed).IsEqualTo(0);
        await AdapterHubCallbacks.DidNotReceiveWithAnyArgs().AdapterConfigurationUpdatedAsync(default!, default!);
    }

    [Test]
    [SuppressMessage("Non-substitutable member", "NS1004:Argument matcher used with a non-virtual member of a class.")]
    public async Task PipelineDisabledByTheUpdate_IsRemovedFromTheAdapter()
    {
        var rtDataFlow = RtEntityCreator.CreateDataFlow();
        var kept = RtEntityCreator.CreatePipeline(OldDefinition);
        var disabled = RtEntityCreator.CreatePipeline("triggers: [{type: FromHttpRequest@2}]");
        var rtAdapter = RtEntityCreator.CreateAdapter();
        rtAdapter.Configuration = "adapter-json";
        InitAdapterConfiguration(rtAdapter, rtDataFlow, [kept, disabled]);
        var pushed = await AdapterService.GetAdapterConfigurationAsync(TenantId, rtAdapter.ToRtEntityId(), true, true);
        AdapterTenant.AddAdapter(rtAdapter.ToRtEntityId(), ConnectionId, pushed);

        var nowDisabled = RtEntityCreator.CreatePipelineFrom(disabled);
        nowDisabled.Enabled = false;
        InitAdapterConfiguration(rtAdapter, rtDataFlow, [RtEntityCreator.CreatePipelineFrom(kept), nowDisabled]);

        var refreshed = await AdapterService.RefreshDeployedPipelinesAsync(TenantId);

        await Assert.That(refreshed).IsEqualTo(1);
        await AdapterHubCallbacks.Received(1).AdapterConfigurationUpdatedAsync(TenantId,
            Arg.Is<AdapterConfigurationDto>(c =>
                c.Pipelines.Count == 1 && c.Pipelines.Single().PipelineRtEntityId == kept.ToRtEntityId()));
        await Assert.That(AdapterTenant.AdapterById[rtAdapter.ToRtEntityId()].Configuration.Pipelines.Count)
            .IsEqualTo(1);
    }

    [Test]
    [SuppressMessage("Non-substitutable member", "NS1004:Argument matcher used with a non-virtual member of a class.")]
    public async Task PipelineDeletedOrMovedOffTheAdapter_IsRemovedFromTheAdapter()
    {
        // Deleted and "moved to another adapter" look the same from this adapter's side: the repository no
        // longer lists the pipeline for it.
        var rtDataFlow = RtEntityCreator.CreateDataFlow();
        var gone = RtEntityCreator.CreatePipeline(OldDefinition);
        var rtAdapter = RtEntityCreator.CreateAdapter();
        rtAdapter.Configuration = "adapter-json";
        InitAdapterConfiguration(rtAdapter, rtDataFlow, [gone]);
        var pushed = await AdapterService.GetAdapterConfigurationAsync(TenantId, rtAdapter.ToRtEntityId(), true, true);
        AdapterTenant.AddAdapter(rtAdapter.ToRtEntityId(), ConnectionId, pushed);

        InitAdapterConfiguration(rtAdapter, rtDataFlow, []);

        var refreshed = await AdapterService.RefreshDeployedPipelinesAsync(TenantId);

        await Assert.That(refreshed).IsEqualTo(1);
        await AdapterHubCallbacks.Received(1).AdapterConfigurationUpdatedAsync(TenantId,
            Arg.Is<AdapterConfigurationDto>(c => c.Pipelines.Count == 0 && c.AdapterConfiguration == "adapter-json"));
        await Assert.That(AdapterTenant.AdapterById[rtAdapter.ToRtEntityId()].Configuration.Pipelines.Count)
            .IsEqualTo(0);
    }

    [Test]
    [SuppressMessage("Non-substitutable member", "NS1004:Argument matcher used with a non-virtual member of a class.")]
    public async Task PipelineThatIsOnlyNoLongerDeployed_StaysWithTheDeployPath()
    {
        // Undeployed/Error states belong to the deploy and undeploy paths; the refresh only removes what is gone.
        var rtDataFlow = RtEntityCreator.CreateDataFlow();
        var running = RtEntityCreator.CreatePipeline(OldDefinition);
        await ArrangeAdapterRunningAsync(ConnectionId, rtDataFlow, running, "adapter-json",
            ToState(running, RtDeploymentStateEnum.Undeployed));

        var refreshed = await AdapterService.RefreshDeployedPipelinesAsync(TenantId);

        await Assert.That(refreshed).IsEqualTo(0);
        await AdapterHubCallbacks.DidNotReceiveWithAnyArgs().AdapterConfigurationUpdatedAsync(default!, default!);
    }

    private static RtPipeline ToState(RtPipeline source, RtDeploymentStateEnum state)
    {
        var copy = RtEntityCreator.CreatePipelineFrom(source);
        copy.DeploymentState = state;
        return copy;
    }

    [Test]
    [SuppressMessage("Non-substitutable member", "NS1004:Argument matcher used with a non-virtual member of a class.")]
    public async Task AdapterWithoutLiveConnection_IsSkipped()
    {
        // It gets the persisted state when it registers; nothing to push to.
        var rtDataFlow = RtEntityCreator.CreateDataFlow();
        var running = RtEntityCreator.CreatePipeline(OldDefinition);
        await ArrangeAdapterRunningAsync("", rtDataFlow, running, "adapter-json",
            RtEntityCreator.CreatePipelineFrom(running, NewDefinition));

        var refreshed = await AdapterService.RefreshDeployedPipelinesAsync(TenantId);

        await Assert.That(refreshed).IsEqualTo(0);
        await AdapterHubCallbacks.DidNotReceiveWithAnyArgs().AdapterConfigurationUpdatedAsync(default!, default!);
    }

    [Test]
    [SuppressMessage("Non-substitutable member", "NS1004:Argument matcher used with a non-virtual member of a class.")]
    public async Task TenantNotLoaded_ReturnsZero()
    {
        AdapterCache.TryGetTenant("unknownTenant", out Arg.Any<AdapterTenant?>()).Returns(false);

        var refreshed = await AdapterService.RefreshDeployedPipelinesAsync("unknownTenant");

        await Assert.That(refreshed).IsEqualTo(0);
    }

    [Test]
    [SuppressMessage("Non-substitutable member", "NS1004:Argument matcher used with a non-virtual member of a class.")]
    public async Task OneAdapterFailing_DoesNotStopTheOthers()
    {
        var rtDataFlow = RtEntityCreator.CreateDataFlow();
        var runningA = RtEntityCreator.CreatePipeline(OldDefinition);
        var runningB = RtEntityCreator.CreatePipeline(OldDefinition);

        var adapterA = await ArrangeAdapterRunningAsync("connA", rtDataFlow, runningA, "a",
            RtEntityCreator.CreatePipelineFrom(runningA, NewDefinition));
        var adapterB = await ArrangeAdapterRunningAsync("connB", rtDataFlow, runningB, "b",
            RtEntityCreator.CreatePipelineFrom(runningB, NewDefinition));
        CommunicationRepository.GetPipelinesAsync(TenantId, adapterA.ToRtEntityId())
            .Returns(Task.FromException<IReadOnlyCollection<RtPipeline>>(
                new InvalidOperationException("repository unavailable")));

        var refreshed = await AdapterService.RefreshDeployedPipelinesAsync(TenantId);

        await Assert.That(refreshed).IsEqualTo(1);
        await AdapterHubCallbacks.Received(1).AdapterConfigurationUpdatedAsync(TenantId,
            Arg.Is<AdapterConfigurationDto>(c => c.AdapterRtEntityId == adapterB.ToRtEntityId()));
    }

    [Test]
    [SuppressMessage("Non-substitutable member", "NS1004:Argument matcher used with a non-virtual member of a class.")]
    public async Task PushThrowing_DoesNotStopTheOthers_AndIsNotCounted()
    {
        var rtDataFlow = RtEntityCreator.CreateDataFlow();
        var runningA = RtEntityCreator.CreatePipeline(OldDefinition);
        var runningB = RtEntityCreator.CreatePipeline(OldDefinition);
        var adapterA = await ArrangeAdapterRunningAsync("connA", rtDataFlow, runningA, "a",
            RtEntityCreator.CreatePipelineFrom(runningA, NewDefinition));
        var adapterB = await ArrangeAdapterRunningAsync("connB", rtDataFlow, runningB, "b",
            RtEntityCreator.CreatePipelineFrom(runningB, NewDefinition));
        AdapterHubCallbacks.AdapterConfigurationUpdatedAsync(TenantId,
                Arg.Is<AdapterConfigurationDto>(c => c.AdapterRtEntityId == adapterA.ToRtEntityId()))
            .Returns(Task.FromException(new InvalidOperationException("hub send failed")));

        var refreshed = await AdapterService.RefreshDeployedPipelinesAsync(TenantId);

        await Assert.That(refreshed).IsEqualTo(1);
        await AdapterHubCallbacks.Received(1).AdapterConfigurationUpdatedAsync(TenantId,
            Arg.Is<AdapterConfigurationDto>(c => c.AdapterRtEntityId == adapterB.ToRtEntityId()));
        await Assert.That(AdapterTenant.AdapterById[adapterB.ToRtEntityId()].Configuration.Pipelines.Single()
            .NodeConfiguration).IsEqualTo(NewDefinition);
        // The failed adapter still holds what it really runs.
        await Assert.That(AdapterTenant.AdapterById[adapterA.ToRtEntityId()].Configuration.Pipelines.Single()
            .NodeConfiguration).IsEqualTo(OldDefinition);
    }

    [Test]
    [SuppressMessage("Non-substitutable member", "NS1004:Argument matcher used with a non-virtual member of a class.")]
    public async Task AdapterWhosePushFailed_IsRetriedByTheNextRefresh()
    {
        var rtDataFlow = RtEntityCreator.CreateDataFlow();
        var running = RtEntityCreator.CreatePipeline(OldDefinition);
        var rtAdapter = await ArrangeAdapterRunningAsync(ConnectionId, rtDataFlow, running, "adapter-json",
            RtEntityCreator.CreatePipelineFrom(running, NewDefinition));
        AdapterHubCallbacks.AdapterConfigurationUpdatedAsync(TenantId, Arg.Any<AdapterConfigurationDto>())
            .Returns(Task.FromException(new InvalidOperationException("hub send failed")),
                Task.CompletedTask);

        var first = await AdapterService.RefreshDeployedPipelinesAsync(TenantId);
        var second = await AdapterService.RefreshDeployedPipelinesAsync(TenantId);

        await Assert.That(first).IsEqualTo(0);
        await Assert.That(second).IsEqualTo(1);
        await Assert.That(AdapterTenant.AdapterById[rtAdapter.ToRtEntityId()].Configuration.Pipelines.Single()
            .NodeConfiguration).IsEqualTo(NewDefinition);
    }

    [Test]
    [SuppressMessage("Non-substitutable member", "NS1004:Argument matcher used with a non-virtual member of a class.")]
    public async Task CacheAlreadyHoldsTheNewConfiguration_WhenTheSendReturns_SoAnImmediateAckSeesIt()
    {
        // The send does not wait for the adapter: a fast ack can arrive before the send call returns.
        var rtDataFlow = RtEntityCreator.CreateDataFlow();
        var running = RtEntityCreator.CreatePipeline(OldDefinition);
        var rtAdapter = await ArrangeAdapterRunningAsync(ConnectionId, rtDataFlow, running, "adapter-json",
            RtEntityCreator.CreatePipelineFrom(running, NewDefinition));
        string? seenDuringSend = null;
        AdapterHubCallbacks.AdapterConfigurationUpdatedAsync(TenantId, Arg.Any<AdapterConfigurationDto>())
            .Returns(_ =>
            {
                seenDuringSend = AdapterTenant.AdapterById[rtAdapter.ToRtEntityId()].Configuration.Pipelines
                    .Single().NodeConfiguration;
                return Task.CompletedTask;
            });

        await AdapterService.RefreshDeployedPipelinesAsync(TenantId);

        await Assert.That(seenDuringSend).IsEqualTo(NewDefinition);
    }

    [Test]
    [SuppressMessage("Non-substitutable member", "NS1004:Argument matcher used with a non-virtual member of a class.")]
    public async Task FailedSend_DoesNotClobberAConcurrentNewerConfiguration()
    {
        var rtDataFlow = RtEntityCreator.CreateDataFlow();
        var running = RtEntityCreator.CreatePipeline(OldDefinition);
        var rtAdapter = await ArrangeAdapterRunningAsync(ConnectionId, rtDataFlow, running, "adapter-json",
            RtEntityCreator.CreatePipelineFrom(running, NewDefinition));
        var newer = new AdapterConfigurationDto(rtAdapter.ToRtEntityId(), "newer-json", []);
        AdapterHubCallbacks.AdapterConfigurationUpdatedAsync(TenantId, Arg.Any<AdapterConfigurationDto>())
            .Returns(_ =>
            {
                AdapterTenant.AdapterById[rtAdapter.ToRtEntityId()].UpdateConfiguration(TenantId, newer);
                return Task.FromException(new InvalidOperationException("hub send failed"));
            });

        var refreshed = await AdapterService.RefreshDeployedPipelinesAsync(TenantId);

        await Assert.That(refreshed).IsEqualTo(0);
        await Assert.That(AdapterTenant.AdapterById[rtAdapter.ToRtEntityId()].Configuration)
            .IsSameReferenceAs(newer);
    }
}
