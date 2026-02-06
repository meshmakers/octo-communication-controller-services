using System.Diagnostics.CodeAnalysis;
using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Adapters;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v2;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.Runtime.Contracts;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.AdapterServiceTests;

internal class GetAdapterConfigurationAsyncTests : AdapterServiceTestsBase
{
    [Test]
    [SuppressMessage("Non-substitutable member", "NS1004:Argument matcher used with a non-virtual member of a class.")]
    public async Task GetAdapterConfigurationAsync_TenantNotInCache_ThrowsException()
    {
        // Arrange
        AdapterCache.TryGetTenant("unknownTenant", out Arg.Any<AdapterTenant?>())
            .Returns(false);

        var rtAdapter = RtEntityCreator.CreateAdapter();

        // Act & Assert - Exception is caught and wrapped
        var exception = await Assert.That(async () =>
                await AdapterService.GetAdapterConfigurationAsync("unknownTenant", rtAdapter.ToRtEntityId(), false))
            .Throws<AdapterServiceException>();

        await Assert.That(exception).IsNotNull()
            .And.Member(e => e.Message, msg => msg.Contains("Failed to load adapter"));

        await Assert.That(exception!.InnerException).IsNotNull()
            .And.Member(e => e!.Message, msg => msg.Contains("Tenant not enabled"));
    }

    [Test]
    public async Task GetAdapterConfigurationAsync_OnlyDeployedPipelines_ReturnsOnlyDeployedPipelines()
    {
        // Arrange
        var rtAdapter = RtEntityCreator.CreateAdapter();
        var rtDataPipeline = RtEntityCreator.CreateDataPipeline();
        var rtPipelineDeployed = RtEntityCreator.CreatePipeline();
        rtPipelineDeployed.DeploymentState = RtDeploymentStateEnum.Deployed;

        var rtPipelinePending = RtEntityCreator.CreatePipeline();
        rtPipelinePending.DeploymentState = RtDeploymentStateEnum.Undeployed;

        CommunicationRepository.GetAdapterAsync(TenantId, rtAdapter.ToRtEntityId())
            .Returns(rtAdapter);
        CommunicationRepository.GetPipelinesAsync(TenantId, rtAdapter.ToRtEntityId())
            .Returns([rtPipelineDeployed, rtPipelinePending]);
        CommunicationRepository.GetDataPipelineByPipelineAsync(TenantId, rtPipelineDeployed.RtId)
            .Returns(rtDataPipeline);
        CommunicationRepository.GetDataPipelineByPipelineAsync(TenantId, rtPipelinePending.RtId)
            .Returns(rtDataPipeline);
        CommunicationRepository.GetConfigurationsByPipelineAsync(TenantId, Arg.Any<OctoObjectId>())
            .Returns([]);

        // Act
        var configuration = await AdapterService.GetAdapterConfigurationAsync(TenantId, rtAdapter.ToRtEntityId(), true);

        // Assert
        using var _ = Assert.Multiple();

        await Assert.That(configuration).IsNotNull();
        await Assert.That(configuration.Pipelines).Count().IsEqualTo(1);
        await Assert.That(configuration.Pipelines.First().PipelineRtEntityId).IsEqualTo(rtPipelineDeployed.ToRtEntityId());
    }

    [Test]
    public async Task GetAdapterConfigurationAsync_AllPipelines_ReturnsAllPipelines()
    {
        // Arrange
        var rtAdapter = RtEntityCreator.CreateAdapter();
        var rtDataPipeline = RtEntityCreator.CreateDataPipeline();
        var rtPipelineDeployed = RtEntityCreator.CreatePipeline();
        rtPipelineDeployed.DeploymentState = RtDeploymentStateEnum.Deployed;

        var rtPipelinePending = RtEntityCreator.CreatePipeline();
        rtPipelinePending.DeploymentState = RtDeploymentStateEnum.Pending;

        CommunicationRepository.GetAdapterAsync(TenantId, rtAdapter.ToRtEntityId())
            .Returns(rtAdapter);
        CommunicationRepository.GetPipelinesAsync(TenantId, rtAdapter.ToRtEntityId())
            .Returns([rtPipelineDeployed, rtPipelinePending]);
        CommunicationRepository.GetDataPipelineByPipelineAsync(TenantId, rtPipelineDeployed.RtId)
            .Returns(rtDataPipeline);
        CommunicationRepository.GetDataPipelineByPipelineAsync(TenantId, rtPipelinePending.RtId)
            .Returns(rtDataPipeline);
        CommunicationRepository.GetConfigurationsByPipelineAsync(TenantId, Arg.Any<OctoObjectId>())
            .Returns([]);

        // Act
        var configuration = await AdapterService.GetAdapterConfigurationAsync(TenantId, rtAdapter.ToRtEntityId(), false);

        // Assert
        using var _ = Assert.Multiple();

        await Assert.That(configuration).IsNotNull();
        await Assert.That(configuration.Pipelines).Count().IsEqualTo(2);

        var deployedPipeline = configuration.Pipelines.FirstOrDefault(p => p.PipelineRtEntityId == rtPipelineDeployed.ToRtEntityId());
        await Assert.That(deployedPipeline).IsNotNull();

        var pendingPipeline = configuration.Pipelines.FirstOrDefault(p => p.PipelineRtEntityId == rtPipelinePending.ToRtEntityId());
        await Assert.That(pendingPipeline).IsNotNull();
    }

    [Test]
    public async Task GetAdapterConfigurationAsync_NoPipelines_ReturnsEmptyPipelineList()
    {
        // Arrange
        var rtAdapter = RtEntityCreator.CreateAdapter();

        CommunicationRepository.GetAdapterAsync(TenantId, rtAdapter.ToRtEntityId())
            .Returns(rtAdapter);
        CommunicationRepository.GetPipelinesAsync(TenantId, rtAdapter.ToRtEntityId())
            .Returns([]);

        // Act
        var configuration = await AdapterService.GetAdapterConfigurationAsync(TenantId, rtAdapter.ToRtEntityId(), false);

        // Assert
        using var _ = Assert.Multiple();

        await Assert.That(configuration).IsNotNull();
        await Assert.That(configuration.Pipelines).Count().IsEqualTo(0);
        await Assert.That(configuration.AdapterRtEntityId).IsEqualTo(rtAdapter.ToRtEntityId());
    }

    [Test]
    public async Task GetAdapterConfigurationAsync_PipelineWithEmptyDefinition_SkipsPipeline()
    {
        // Arrange
        var rtAdapter = RtEntityCreator.CreateAdapter();
        var rtDataPipeline = RtEntityCreator.CreateDataPipeline();
        var rtPipelineWithDefinition = RtEntityCreator.CreatePipeline();
        rtPipelineWithDefinition.DeploymentState = RtDeploymentStateEnum.Deployed;

        var rtPipelineWithEmptyDefinition = RtEntityCreator.CreatePipeline();
        rtPipelineWithEmptyDefinition.PipelineDefinition = "   ";
        rtPipelineWithEmptyDefinition.DeploymentState = RtDeploymentStateEnum.Deployed;

        CommunicationRepository.GetAdapterAsync(TenantId, rtAdapter.ToRtEntityId())
            .Returns(rtAdapter);
        CommunicationRepository.GetPipelinesAsync(TenantId, rtAdapter.ToRtEntityId())
            .Returns([rtPipelineWithDefinition, rtPipelineWithEmptyDefinition]);
        CommunicationRepository.GetDataPipelineByPipelineAsync(TenantId, rtPipelineWithDefinition.RtId)
            .Returns(rtDataPipeline);
        CommunicationRepository.GetConfigurationsByPipelineAsync(TenantId, rtPipelineWithDefinition.RtId)
            .Returns(Task.FromResult<IEnumerable<RtConfiguration>>([]));

        // Act
        var configuration = await AdapterService.GetAdapterConfigurationAsync(TenantId, rtAdapter.ToRtEntityId(), false);

        // Assert
        using var _ = Assert.Multiple();

        await Assert.That(configuration).IsNotNull();
        await Assert.That(configuration.Pipelines).Count().IsEqualTo(1);
        await Assert.That(configuration.Pipelines.First().PipelineRtEntityId).IsEqualTo(rtPipelineWithDefinition.ToRtEntityId());
    }

    [Test]
    public async Task GetAdapterConfigurationAsync_DataPipelineNotFound_SkipsPipeline()
    {
        // Arrange
        var rtAdapter = RtEntityCreator.CreateAdapter();
        var rtPipeline = RtEntityCreator.CreatePipeline();

        CommunicationRepository.GetAdapterAsync(TenantId, rtAdapter.ToRtEntityId())
            .Returns(rtAdapter);
        CommunicationRepository.GetPipelinesAsync(TenantId, rtAdapter.ToRtEntityId())
            .Returns([rtPipeline]);
        CommunicationRepository.GetDataPipelineByPipelineAsync(TenantId, rtPipeline.RtId)
            .Returns((RtDataPipeline?)null);

        // Act
        var configuration = await AdapterService.GetAdapterConfigurationAsync(TenantId, rtAdapter.ToRtEntityId(), false);

        // Assert - pipeline without DataPipeline should be skipped
        await Assert.That(configuration).IsNotNull();
        await Assert.That(configuration.Pipelines).IsEmpty();
    }

    [Test]
    public async Task GetAdapterConfigurationAsync_RepositoryThrowsException_WrapsInAdapterServiceException()
    {
        // Arrange
        var rtAdapter = RtEntityCreator.CreateAdapter();
        var expectedException = new InvalidOperationException("Database connection failed");

        CommunicationRepository.GetAdapterAsync(TenantId, rtAdapter.ToRtEntityId())
            .Returns<RtAdapter>(_ => throw expectedException);

        // Act & Assert
        var exception = await Assert.That(async () =>
                await AdapterService.GetAdapterConfigurationAsync(TenantId, rtAdapter.ToRtEntityId(), false))
            .Throws<AdapterServiceException>();

        await Assert.That(exception).IsNotNull()
            .And.Member(e => e.Message, msg => msg.Contains("Failed to load adapter").And.Contains("configuration"));

        await Assert.That(exception).IsNotNull()
            .And.Member(e => e.InnerException, inner => inner.IsEqualTo(expectedException));
    }

    [Test]
    public async Task GetAdapterConfigurationAsync_WithPipelineConfigurations_IncludesConfigurations()
    {
        // Arrange
        var rtAdapter = RtEntityCreator.CreateAdapter();
        var rtDataPipeline = RtEntityCreator.CreateDataPipeline();
        var rtPipeline = RtEntityCreator.CreatePipeline();

        var configuration1 = new RtConfiguration
        {
            RtId = OctoObjectId.GenerateNewId(),
            CkTypeId = SystemCkIds.RtCkConfigurationTypeId,
            RtWellKnownName = "config1"
        };

        var configuration2 = new RtConfiguration
        {
            RtId = OctoObjectId.GenerateNewId(),
            CkTypeId = SystemCkIds.RtCkConfigurationTypeId,
            RtWellKnownName = "config2"
        };

        CommunicationRepository.GetAdapterAsync(TenantId, rtAdapter.ToRtEntityId())
            .Returns(rtAdapter);
        CommunicationRepository.GetPipelinesAsync(TenantId, rtAdapter.ToRtEntityId())
            .Returns([rtPipeline]);
        CommunicationRepository.GetDataPipelineByPipelineAsync(TenantId, rtPipeline.RtId)
            .Returns(rtDataPipeline);
        CommunicationRepository.GetConfigurationsByPipelineAsync(TenantId, rtPipeline.RtId)
            .Returns(Task.FromResult<IEnumerable<RtConfiguration>>([configuration1, configuration2]));

        // Act
        var configuration = await AdapterService.GetAdapterConfigurationAsync(TenantId, rtAdapter.ToRtEntityId(), false);

        // Assert
        using var _ = Assert.Multiple();

        await Assert.That(configuration).IsNotNull();
        await Assert.That(configuration.Pipelines).Count().IsEqualTo(1);

        var pipeline = configuration.Pipelines.First();
        await Assert.That(pipeline.Configurations).Count().IsEqualTo(2);;
        await Assert.That(pipeline.Configurations.Select(c => c.ConfigurationRtId))
            .Contains(configuration1.RtId)
            .And.Contains(configuration2.RtId);
    }


    [Test]
    public async Task GetAdapterConfigurationAsync_IncludesAdapterConfiguration()
    {
        // Arrange
        var rtAdapter = RtEntityCreator.CreateAdapter();
        rtAdapter.Configuration = "{ \"setting\": \"value\" }";

        CommunicationRepository.GetAdapterAsync(TenantId, rtAdapter.ToRtEntityId())
            .Returns(rtAdapter);
        CommunicationRepository.GetPipelinesAsync(TenantId, rtAdapter.ToRtEntityId())
            .Returns([]);

        // Act
        var configuration = await AdapterService.GetAdapterConfigurationAsync(TenantId, rtAdapter.ToRtEntityId(), false);

        // Assert
        using var _ = Assert.Multiple();

        await Assert.That(configuration).IsNotNull();
        await Assert.That(configuration.AdapterConfiguration).IsEqualTo(rtAdapter.Configuration);
        await Assert.That(configuration.AdapterRtEntityId).IsEqualTo(rtAdapter.ToRtEntityId());
    }
}
