using FluentAssertions;
using Meshmakers.Octo.Backend.CommunicationControllerServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.Secrets;
using NSubstitute;
using Xunit;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.IntegrationTests.Services;

/// <summary>
///     A tenant deletes a projected SecretDefault with the generic delete, which archives it. The next reconcile
///     restores it, and the one after that writes nothing and stores no event.
/// </summary>
[Collection("CommunicationController")]
public class SecretDefaultArchivedRestoreTests(CommunicationControllerFixture fixture)
{
    private const string ProjectedPlaintext = "zq7245-integration-archived-restore";

    private readonly ICommunicationEventService _communicationEventService =
        Substitute.For<ICommunicationEventService>();

    private SecretDefaultProvisioningService CreateService()
    {
        var tenantScopeResolver = Substitute.For<ITenantLendingScopeResolver>();
        tenantScopeResolver.ResolveAncestorTenantsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyCollection<string>)[]);
        return new SecretDefaultProvisioningService(fixture.GetService<ICommunicationRepository>(),
            tenantScopeResolver, fixture.GetService<IWorkloadEncryptionService>(), _communicationEventService,
            TimeProvider.System);
    }

    private async Task<RtApplication> InsertDeployedApplicationAsync(string key)
    {
        var application = new RtApplication
        {
            RtId = OctoObjectId.GenerateNewId(),
            CkTypeId = SystemCommunicationCkIds.RtCkApplicationTypeId,
            Name = $"archived-restore-{Guid.NewGuid():N}",
            DeploymentState = RtDeploymentStateEnum.Deployed,
            SecretDefaults = new AttributeRecordValueList<RtSecretDefaultProjectionRecord>
            {
                new() { Key = key, SecretValue = RtSecretValue.Pending(ProjectedPlaintext) }
            }
        };
        await RunInTransactionAsync((tenantRepository, session) =>
            tenantRepository.InsertOneRtEntityAsync(session, application));
        return application;
    }

    private async Task RunInTransactionAsync(Func<ITenantRepository, IOctoSession, Task> action)
    {
        var tenantRepository = await fixture.GetSystemContext().FindTenantRepositoryAsync(fixture.TestTenantId);
        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();
        await action(tenantRepository, session);
        await session.CommitTransactionAsync();
    }

    private Task ArchiveAsync(OctoObjectId rtId)
    {
        return RunInTransactionAsync((tenantRepository, session) =>
            tenantRepository.DeleteOneRtEntityByRtIdAsync<RtSecretDefault>(session, rtId, DeleteOptions.Default));
    }

    private async Task<StoredSecretDefault?> ListedDefaultAsync(string key)
    {
        var inventory = await fixture.GetService<ICommunicationRepository>().GetSecretDefaultsAsync(fixture.TestTenantId);
        return inventory.Defaults.SingleOrDefault(storedDefault => storedDefault.Key == key);
    }

    [Fact]
    public async Task ProvisionForTargetAsync_AnArchivedDefault_IsRestoredAndTheNextReconcileWritesNothing()
    {
        var key = $"archived-restore-{Guid.NewGuid():N}";
        var service = CreateService();
        var application = await InsertDeployedApplicationAsync(key);
        try
        {
            await service.ProvisionForTargetAsync(fixture.TestTenantId, TestContext.Current.CancellationToken);
            var projected = await ListedDefaultAsync(key);
            projected.Should().NotBeNull();
            await ArchiveAsync(projected!.RtId);
            (await ListedDefaultAsync(key)).Should().BeNull("the archived default is gone for the tenant");

            var restoringResult = await service.ProvisionForTargetAsync(fixture.TestTenantId, TestContext.Current.CancellationToken);
            var restored = await ListedDefaultAsync(key);
            _communicationEventService.ClearReceivedCalls();
            var settledResult = await service.ProvisionForTargetAsync(fixture.TestTenantId, TestContext.Current.CancellationToken);

            restoringResult.DefaultsCreated.Should().Be(1);
            restored.Should().NotBeNull();
            restored!.SourceWorkloadRtId.Should().Be(application.RtId.ToString());
            fixture.GetService<ISecretAttributeProtector>().Unprotect(restored.Value!).Should().Be(ProjectedPlaintext);
            settledResult.DefaultsWritten.Should().Be(0);
            settledResult.DefaultsRemoved.Should().Be(0);
            settledResult.WritesFailed.Should().Be(0);
            _communicationEventService.ReceivedCalls().Should().BeEmpty();
        }
        finally
        {
            await RunInTransactionAsync((tenantRepository, session) =>
                tenantRepository.DeleteOneRtEntityByRtIdAsync<RtApplication>(session, application.RtId,
                    DeleteOptions.Erase));
            await service.ProvisionForTargetAsync(fixture.TestTenantId, TestContext.Current.CancellationToken);
        }
    }
}
