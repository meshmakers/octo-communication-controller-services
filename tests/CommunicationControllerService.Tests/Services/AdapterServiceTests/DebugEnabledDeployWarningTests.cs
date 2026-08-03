using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.AdapterServiceTests;

/// <summary>
/// AB#4662: a lingering debug flag on a heavy pipeline OOM-killed the adapter without anyone
/// knowing debug capture was active. Every configuration push must surface debug-enabled
/// pipelines as a Warning event; pushes without debug-enabled pipelines must stay silent.
/// </summary>
internal class DebugEnabledDeployWarningTests : AdapterServiceTestsBase
{
    [Test]
    public async Task DeployAdapterConfiguration_DebugEnabledPipeline_StoresWarningEvent()
    {
        // Arrange
        var rtAdapter = RtEntityCreator.CreateAdapter();
        var rtDataFlow = RtEntityCreator.CreateDataFlow();
        var rtPipeline = RtEntityCreator.CreatePipeline();
        rtPipeline.IsDebuggingEnabled = true;

        InitAdapterConfiguration(rtAdapter, rtDataFlow, [rtPipeline]);
        CommunicationRepository.GetConfigurationsByPipelineAsync(TenantId, Arg.Any<OctoObjectId>())
            .Returns([]);

        AdapterTenant.AddAdapter(rtAdapter.ToRtEntityId(), ConnectionId,
            new AdapterConfigurationDto(rtAdapter.ToRtEntityId(), "old-json", []));

        // Act
        await AdapterService.DeployAdapterConfigurationAsync(TenantId, rtAdapter.ToRtEntityId());

        // Assert
        await CommunicationEventService.Received(1).StoreWarningEventAsync(TenantId,
            Arg.Is<string>(msg => msg.Contains("debug capture enabled")),
            rtPipeline.ToRtEntityId());
    }

    [Test]
    public async Task DeployAdapterConfiguration_NoDebugEnabledPipeline_StoresNoWarningEvent()
    {
        // Arrange
        var rtAdapter = RtEntityCreator.CreateAdapter();
        var rtDataFlow = RtEntityCreator.CreateDataFlow();
        var rtPipeline = RtEntityCreator.CreatePipeline();
        rtPipeline.IsDebuggingEnabled = false;

        InitAdapterConfiguration(rtAdapter, rtDataFlow, [rtPipeline]);
        CommunicationRepository.GetConfigurationsByPipelineAsync(TenantId, Arg.Any<OctoObjectId>())
            .Returns([]);

        AdapterTenant.AddAdapter(rtAdapter.ToRtEntityId(), ConnectionId,
            new AdapterConfigurationDto(rtAdapter.ToRtEntityId(), "old-json", []));

        // Act
        await AdapterService.DeployAdapterConfigurationAsync(TenantId, rtAdapter.ToRtEntityId());

        // Assert
        await CommunicationEventService.DidNotReceive().StoreWarningEventAsync(TenantId,
            Arg.Is<string>(msg => msg.Contains("debug capture enabled")),
            Arg.Any<RtEntityId?>());
    }
}
