using Meshmakers.Octo.Backend.CommunicationControllerServices.Hubs;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.WorkloadLifecycleServiceTests;

internal class RequestScaleAsyncTests
{
    private const string TenantId = "meshtest";
    private const string WorkloadRtId = "66004fda527ac79a03ecedd7";
    private const string DeploymentSiteRtId = "65d5c447b420da3fb12381bc";

    private readonly ICommunicationRepository _repository =
        Substitute.For<ICommunicationRepository>();
    private readonly ICommunicationEventService _eventService =
        Substitute.For<ICommunicationEventService>();
    private readonly IOperatorConnectionManager _connectionManager =
        Substitute.For<IOperatorConnectionManager>();
    private readonly WorkloadLifecycleService _service;

    public RequestScaleAsyncTests()
    {
        _service = new WorkloadLifecycleService(
            Substitute.For<ILogger<WorkloadLifecycleService>>(),
            _repository, _eventService, _connectionManager,
            Substitute.For<ILifecycleConfigurationService>(),
            Microsoft.Extensions.Options.Options.Create(new Meshmakers.Octo.Backend.CommunicationControllerServices.Options.CommunicationControllerOptions()));
    }

    private void GivenWorkloadIsInPool()
    {
        var pool = new RtDeploymentSite
        {
            RtId = new OctoObjectId(DeploymentSiteRtId),
            CkTypeId = SystemCommunicationCkIds.RtCkDeploymentSiteTypeId,
            Name = "cloud-pool",
            Environment = RtEnvironmentEnum.Cloud,
        };
        _repository.GetPoolForWorkloadAsync(TenantId, Arg.Is<OctoObjectId>(id => id.ToString() == WorkloadRtId))
            .Returns(pool);
    }

    [Test]
    public async Task WorkloadWithoutPool_Throws()
    {
        // Repository returns null (default) — the workload is not assigned to
        // any pool, so there is no operator to route the scale request to.
        var adapter = new RtAdapter
        {
            RtId = new OctoObjectId(WorkloadRtId),
            CkTypeId = SystemCommunicationCkIds.RtCkAdapterTypeId,
            Name = "meshtest-adapter",
        };

        await Assert.ThrowsAsync<DeploymentSiteServiceException>(
            () => _service.RequestScaleAsync(TenantId, adapter, 0));

        await _connectionManager.DidNotReceiveWithAnyArgs().NotifyWorkloadScaleAsync(
            Arg.Any<ScaleWorkloadDto>());
    }

    [Test]
    public async Task Adapter_NotifiesScaleWithPoolRoutingAndReplicas()
    {
        GivenWorkloadIsInPool();
        var adapter = new RtAdapter
        {
            RtId = new OctoObjectId(WorkloadRtId),
            CkTypeId = SystemCommunicationCkIds.RtCkAdapterTypeId,
            Name = "meshtest-adapter",
        };

        await _service.RequestScaleAsync(TenantId, adapter, 0);

        await _connectionManager.Received(1).NotifyWorkloadScaleAsync(Arg.Is<ScaleWorkloadDto>(dto =>
            dto.TenantId == TenantId
            && dto.DeploymentSiteRtId == DeploymentSiteRtId
            && dto.WorkloadRtId == WorkloadRtId
            && dto.WorkloadName == "meshtest-adapter"
            && dto.WorkloadType == WorkloadTypeDto.Adapter
            && dto.Replicas == 0));
    }

    [Test]
    public async Task Application_MapsToApplicationWorkloadType()
    {
        GivenWorkloadIsInPool();
        var application = new RtApplication
        {
            RtId = new OctoObjectId(WorkloadRtId),
            CkTypeId = SystemCommunicationCkIds.RtCkApplicationTypeId,
            Name = "meshtest-app",
        };

        await _service.RequestScaleAsync(TenantId, application, 1);

        await _connectionManager.Received(1).NotifyWorkloadScaleAsync(Arg.Is<ScaleWorkloadDto>(dto =>
            dto.WorkloadType == WorkloadTypeDto.Application
            && dto.WorkloadName == "meshtest-app"
            && dto.Replicas == 1));
    }

    [Test]
    public async Task WorkloadWithoutName_SendsEmptyWorkloadName()
    {
        GivenWorkloadIsInPool();
        var adapter = new RtAdapter
        {
            RtId = new OctoObjectId(WorkloadRtId),
            CkTypeId = SystemCommunicationCkIds.RtCkAdapterTypeId,
        };

        await _service.RequestScaleAsync(TenantId, adapter, 1);

        await _connectionManager.Received(1).NotifyWorkloadScaleAsync(
            Arg.Is<ScaleWorkloadDto>(dto => dto.WorkloadName == string.Empty));
    }

    private static RtAdapterPool Pool(int minReplicas, int maxReplicas) => new()
    {
        RtId = new OctoObjectId(WorkloadRtId),
        CkTypeId = SystemCommunicationCkIds.RtCkAdapterPoolTypeId,
        Name = "meshtest-pool",
        MinReplicas = minReplicas,
        MaxReplicas = maxReplicas,
    };

    [Test]
    public async Task AdapterPool_MapsToAdapterPoolWorkloadType()
    {
        GivenWorkloadIsInPool();

        await _service.RequestScaleAsync(TenantId, Pool(minReplicas: 1, maxReplicas: 3), 2);

        await _connectionManager.Received(1).NotifyWorkloadScaleAsync(Arg.Is<ScaleWorkloadDto>(dto =>
            dto.WorkloadType == WorkloadTypeDto.AdapterPool
            && dto.WorkloadName == "meshtest-pool"
            && dto.Replicas == 2));
    }

    [Test]
    public async Task AdapterPool_ScaleToZero_IsHeldAtMinReplicas()
    {
        // 🔴 AB#4924 §7.3. MinReplicas is the floor of the pool's own lifecycle: at least one
        // member stays hot so the first work item does not pay a cold start. A scale-to-0 request
        // — from the idle path or from anywhere else — is a request the pool cannot honour.
        GivenWorkloadIsInPool();

        await _service.RequestScaleAsync(TenantId, Pool(minReplicas: 1, maxReplicas: 3), 0);

        await _connectionManager.Received(1).NotifyWorkloadScaleAsync(
            Arg.Is<ScaleWorkloadDto>(dto => dto.Replicas == 1));
    }

    [Test]
    public async Task AdapterPool_ScaleAboveMaxReplicas_IsHeldAtMaxReplicas()
    {
        GivenWorkloadIsInPool();

        await _service.RequestScaleAsync(TenantId, Pool(minReplicas: 1, maxReplicas: 3), 9);

        await _connectionManager.Received(1).NotifyWorkloadScaleAsync(
            Arg.Is<ScaleWorkloadDto>(dto => dto.Replicas == 3));
    }

    [Test]
    public async Task AdapterPoolWithMinReplicasZero_MayScaleToZero()
    {
        // MinReplicas=0 is a valid, deliberate choice (a scale-to-zero pool where every burst pays
        // one cold start), so the floor must be the declared value and not a hardcoded 1.
        GivenWorkloadIsInPool();

        await _service.RequestScaleAsync(TenantId, Pool(minReplicas: 0, maxReplicas: 3), 0);

        await _connectionManager.Received(1).NotifyWorkloadScaleAsync(
            Arg.Is<ScaleWorkloadDto>(dto => dto.Replicas == 0));
    }

    [Test]
    public async Task Adapter_ScaleToZero_IsNotClamped()
    {
        // The clamp is a property of a pool's replica range, not of the scale verb. Hibernation of
        // an ordinary adapter is still exactly scale-to-0.
        GivenWorkloadIsInPool();
        var adapter = new RtAdapter
        {
            RtId = new OctoObjectId(WorkloadRtId),
            CkTypeId = SystemCommunicationCkIds.RtCkAdapterTypeId,
            Name = "meshtest-adapter",
        };

        await _service.RequestScaleAsync(TenantId, adapter, 0);

        await _connectionManager.Received(1).NotifyWorkloadScaleAsync(
            Arg.Is<ScaleWorkloadDto>(dto => dto.Replicas == 0));
    }
}
