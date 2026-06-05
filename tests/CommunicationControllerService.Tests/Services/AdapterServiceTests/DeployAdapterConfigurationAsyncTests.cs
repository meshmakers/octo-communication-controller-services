using System.Diagnostics.CodeAnalysis;
using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Adapters;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Contracts.Hubs;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v3;
using Meshmakers.Octo.Runtime.Contracts;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.AdapterServiceTests;

internal class DeployAdapterConfigurationAsyncTests : AdapterServiceTestsBase
{
    [Test]
    [SuppressMessage("Non-substitutable member", "NS1004:Argument matcher used with a non-virtual member of a class.")]
    public async Task DeployAdapterConfigurationAsync_TenantNotInCache_ThrowsAdapterNotLoaded()
    {
        // Arrange
        AdapterCache.TryGetTenant("unknownTenant", out Arg.Any<AdapterTenant?>())
            .Returns(false);

        var rtAdapter = RtEntityCreator.CreateAdapter();

        // Act & Assert
        var exception = await Assert.That(async () =>
                await AdapterService.DeployAdapterConfigurationAsync("unknownTenant", rtAdapter.ToRtEntityId()))
            .Throws<AdapterServiceException>();

        await Assert.That(exception).IsNotNull()
            .And.Member(e => e.Message, msg => msg.Contains("no live SignalR connection"));
    }

    [Test]
    public async Task DeployAdapterConfigurationAsync_AdapterNotInCache_ThrowsAdapterNotLoaded()
    {
        // Arrange - tenant in cache but no adapter loaded
        var rtAdapter = RtEntityCreator.CreateAdapter();

        // Act & Assert
        var exception = await Assert.That(async () =>
                await AdapterService.DeployAdapterConfigurationAsync(TenantId, rtAdapter.ToRtEntityId()))
            .Throws<AdapterServiceException>();

        await Assert.That(exception).IsNotNull()
            .And.Member(e => e.Message, msg => msg.Contains("no live SignalR connection"));
    }

    [Test]
    public async Task DeployAdapterConfigurationAsync_PushesUpdatedConfiguration()
    {
        // Arrange - cached config carries old JSON, DB has the new JSON
        var rtAdapter = RtEntityCreator.CreateAdapter();
        rtAdapter.Configuration = "new-json";

        var rtDataFlow = RtEntityCreator.CreateDataFlow();
        var rtPipeline = RtEntityCreator.CreatePipeline();

        InitAdapterConfiguration(rtAdapter, rtDataFlow, [rtPipeline]);
        CommunicationRepository.GetConfigurationsByPipelineAsync(TenantId, Arg.Any<OctoObjectId>())
            .Returns([]);

        AdapterTenant.AddAdapter(rtAdapter.ToRtEntityId(), ConnectionId, new AdapterConfigurationDto(
            rtAdapter.ToRtEntityId(),
            "old-json",
            []
        ));

        // Act
        await AdapterService.DeployAdapterConfigurationAsync(TenantId, rtAdapter.ToRtEntityId());

        // Assert
        await AdapterHubCallbacks.Received(1).AdapterConfigurationUpdatedAsync(TenantId,
            Arg.Is<AdapterConfigurationDto>(config =>
                config.AdapterRtEntityId == rtAdapter.ToRtEntityId() &&
                config.AdapterConfiguration == "new-json" &&
                config.Pipelines.Count == 1));
    }

    /// <summary>
    /// Regression guard for the "configuration is up to date" silent-skip bug.
    /// When the controller's cached config already matches the DB, the user's
    /// explicit "Update Configuration" click must still push to the live pod —
    /// the cache can drift (failed prior push, replaced pod) and the user has
    /// no other way to force a re-sync.
    /// </summary>
    [Test]
    public async Task DeployAdapterConfigurationAsync_CacheMatchesDb_StillPushes()
    {
        // Arrange - cache and DB both already carry the same JSON / pipeline set
        var rtAdapter = RtEntityCreator.CreateAdapter();
        rtAdapter.Configuration = "same-json";

        var rtDataFlow = RtEntityCreator.CreateDataFlow();
        var rtPipeline = RtEntityCreator.CreatePipeline();

        InitAdapterConfiguration(rtAdapter, rtDataFlow, [rtPipeline]);
        CommunicationRepository.GetConfigurationsByPipelineAsync(TenantId, Arg.Any<OctoObjectId>())
            .Returns([]);

        AdapterTenant.AddAdapter(rtAdapter.ToRtEntityId(), ConnectionId, new AdapterConfigurationDto(
            rtAdapter.ToRtEntityId(),
            "same-json",
            [
                new PipelineConfigurationDto(rtDataFlow.RtId, rtPipeline.ToRtEntityId(), false,
                    rtPipeline.PipelineDefinition, [])
            ]
        ));

        // Act
        await AdapterService.DeployAdapterConfigurationAsync(TenantId, rtAdapter.ToRtEntityId());

        // Assert
        await AdapterHubCallbacks.Received(1).AdapterConfigurationUpdatedAsync(TenantId,
            Arg.Is<AdapterConfigurationDto>(config =>
                config.AdapterRtEntityId == rtAdapter.ToRtEntityId() &&
                config.AdapterConfiguration == "same-json"));
    }

    /// <summary>
    /// Failure-isolation guard: a push that throws (adapter pod rejects, times
    /// out, etc.) must leave the cache untouched, so a retry has an accurate
    /// baseline. Pre-fix the cache was updated optimistically before the push,
    /// which masked failures and made retries silently no-op.
    /// </summary>
    [Test]
    [SuppressMessage("Non-substitutable member", "NS1004:Argument matcher used with a non-virtual member of a class.")]
    public async Task DeployAdapterConfigurationAsync_PushFails_CacheUnchanged()
    {
        // Arrange
        var rtAdapter = RtEntityCreator.CreateAdapter();
        rtAdapter.Configuration = "new-json";

        var rtDataFlow = RtEntityCreator.CreateDataFlow();
        var rtPipeline = RtEntityCreator.CreatePipeline();

        InitAdapterConfiguration(rtAdapter, rtDataFlow, [rtPipeline]);
        CommunicationRepository.GetConfigurationsByPipelineAsync(TenantId, Arg.Any<OctoObjectId>())
            .Returns([]);

        var cachedConfig = new AdapterConfigurationDto(rtAdapter.ToRtEntityId(), "old-json", []);
        var adapter = AdapterTenant.AddAdapter(rtAdapter.ToRtEntityId(), ConnectionId, cachedConfig);

        // Override the base-class success callback with a failure callback so
        // SendConfigurationAndWaitForResultAsync throws.
        AdapterHubCallbacks.AdapterConfigurationUpdatedAsync(Arg.Any<string>(), Arg.Any<AdapterConfigurationDto>())
            .Returns(callInfo =>
            {
                var pushed = callInfo.Arg<AdapterConfigurationDto>();
                _ = Task.Run(async () =>
                {
                    await AdapterService.UpdateConfigurationStateAsync(TenantId, pushed.AdapterRtEntityId,
                        new DeploymentResult { IsSuccess = false });
                });
                return Task.CompletedTask;
            });

        // Act & Assert - push throws
        await Assert.That(async () =>
                await AdapterService.DeployAdapterConfigurationAsync(TenantId, rtAdapter.ToRtEntityId()))
            .Throws<AdapterServiceException>();

        // Cache still carries the old config — retry has a correct baseline.
        await Assert.That(adapter.Configuration.AdapterConfiguration).IsEqualTo("old-json");
    }

    [Test]
    public async Task DeployAdapterConfigurationAsync_PushSucceeds_CacheReflectsPushedConfig()
    {
        // Arrange
        var rtAdapter = RtEntityCreator.CreateAdapter();
        rtAdapter.Configuration = "new-json";

        var rtDataFlow = RtEntityCreator.CreateDataFlow();
        var rtPipeline = RtEntityCreator.CreatePipeline();

        InitAdapterConfiguration(rtAdapter, rtDataFlow, [rtPipeline]);
        CommunicationRepository.GetConfigurationsByPipelineAsync(TenantId, Arg.Any<OctoObjectId>())
            .Returns([]);

        var adapter = AdapterTenant.AddAdapter(rtAdapter.ToRtEntityId(), ConnectionId,
            new AdapterConfigurationDto(rtAdapter.ToRtEntityId(), "old-json", []));

        // Act
        await AdapterService.DeployAdapterConfigurationAsync(TenantId, rtAdapter.ToRtEntityId());

        // Assert - the cache now holds the just-pushed config.
        await Assert.That(adapter.Configuration.AdapterConfiguration).IsEqualTo("new-json");
        await Assert.That(adapter.Configuration.Pipelines).Count().IsEqualTo(1);
    }
}
