using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Clusters;
using MongoDB.Driver.Core.Connections;
using MongoDB.Driver.Core.Servers;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Repository;

/// <summary>
/// AB#6435: <see cref="CommunicationRepository"/> repeats a communication-state write once or twice when
/// MongoDB aborts it with a write conflict (seen on test-2, tenant wwc26, 2026-10-10 16:16:05Z) and does not
/// repeat any other failure.
/// </summary>
internal class CommunicationStateWriteRetryTests
{
    private const string TenantId = "wwc26";

    private readonly ITenantRepository _tenantRepository = Substitute.For<ITenantRepository>();
    private readonly CommunicationRepository _sut;

    public CommunicationStateWriteRetryTests()
    {
        var systemContext = Substitute.For<ISystemContext>();
        systemContext.FindTenantRepositoryAsync(TenantId).Returns(_tenantRepository);
        _tenantRepository.GetSessionAsync().Returns(_ => Task.FromResult(Substitute.For<IOctoSession>()));
        _sut = new CommunicationRepository(systemContext, NullLogger<CommunicationRepository>.Instance);
    }

    private static MongoCommandException WriteConflict() =>
        new(new ConnectionId(new ServerId(new ClusterId(1), new System.Net.DnsEndPoint("localhost", 27017))),
            "Write conflict during plan execution", new BsonDocument(),
            new BsonDocument { { "code", 112 }, { "codeName", "WriteConflict" } });

    private int ApplyCalls() => _tenantRepository.ReceivedCalls()
        .Count(c => c.GetMethodInfo().Name == nameof(ITenantRepository.ApplyChangesAsync));

    [Test]
    public async Task SetDeploymentSiteCommunicationState_WriteConflictOnce_RetriesAndSucceeds()
    {
        _tenantRepository
            .ApplyChangesAsync(Arg.Any<IOctoSession>(), Arg.Any<IReadOnlyList<IEntityUpdateInfo<RtEntity>>>(),
                Arg.Any<OperationResult>())
            .Returns(Task.FromException(WriteConflict()), Task.CompletedTask);

        await _sut.SetDeploymentSiteCommunicationStateAsync(TenantId, OctoObjectId.GenerateNewId(),
            RtCommunicationStateEnum.Online);

        await Assert.That(ApplyCalls()).IsEqualTo(2);
    }

    [Test]
    public async Task SetAdapterCommunicationState_WriteConflictOnce_RetriesAndSucceeds()
    {
        _tenantRepository
            .ApplyChangesAsync(Arg.Any<IOctoSession>(), Arg.Any<IReadOnlyList<IEntityUpdateInfo<RtEntity>>>(),
                Arg.Any<OperationResult>())
            .Returns(Task.FromException(WriteConflict()), Task.CompletedTask);

        await _sut.SetAdapterCommunicationStateAsync(TenantId,
            new RtEntityId(SystemCommunicationCkIds.RtCkAdapterTypeId, OctoObjectId.GenerateNewId()),
            RtCommunicationStateEnum.Offline);

        await Assert.That(ApplyCalls()).IsEqualTo(2);
    }

    [Test]
    public async Task SetDeploymentSiteCommunicationState_PersistentConflict_GivesUpAfterTwoRetries()
    {
        _tenantRepository
            .ApplyChangesAsync(Arg.Any<IOctoSession>(), Arg.Any<IReadOnlyList<IEntityUpdateInfo<RtEntity>>>(),
                Arg.Any<OperationResult>())
            .Returns(_ => Task.FromException(WriteConflict()));

        await Assert.ThrowsAsync<CommunicationRepositoryException>(() =>
            _sut.SetDeploymentSiteCommunicationStateAsync(TenantId, OctoObjectId.GenerateNewId(),
                RtCommunicationStateEnum.Online));

        await Assert.That(ApplyCalls()).IsEqualTo(3);
    }

    [Test]
    public async Task SetDeploymentSiteCommunicationState_NonTransientError_IsNotRetried()
    {
        _tenantRepository
            .ApplyChangesAsync(Arg.Any<IOctoSession>(), Arg.Any<IReadOnlyList<IEntityUpdateInfo<RtEntity>>>(),
                Arg.Any<OperationResult>())
            .Returns(_ => Task.FromException(new MongoException("E11000 duplicate key")));

        await Assert.ThrowsAsync<CommunicationRepositoryException>(() =>
            _sut.SetDeploymentSiteCommunicationStateAsync(TenantId, OctoObjectId.GenerateNewId(),
                RtCommunicationStateEnum.Online));

        await Assert.That(ApplyCalls()).IsEqualTo(1);
    }
}
