using Meshmakers.Octo.Backend.CommunicationControllerServices.Hubs;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v3;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.WorkloadLifecycleServiceTests;

internal class RequestScaleAsyncTests
{
    private const string TenantId = "meshtest";
    private const string WorkloadRtId = "66004fda527ac79a03ecedd7";
    private const string PoolRtId = "65d5c447b420da3fb12381bc";

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
        var pool = new RtPool
        {
            RtId = new OctoObjectId(PoolRtId),
            CkTypeId = SystemCommunicationCkIds.RtCkPoolTypeId,
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

        await Assert.ThrowsAsync<PoolServiceException>(
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
            && dto.PoolRtId == PoolRtId
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
}
