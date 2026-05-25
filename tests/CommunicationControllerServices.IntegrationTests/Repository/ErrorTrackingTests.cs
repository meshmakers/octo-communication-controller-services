using FluentAssertions;
using Meshmakers.Octo.Backend.CommunicationControllerServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v3;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Xunit;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.IntegrationTests.Repository;

/// <summary>
/// Integration tests for <see cref="ICommunicationRepository.SetAdapterDeploymentStateAsync"/>
/// and <see cref="ICommunicationRepository.SetAdapterConfigurationStateAsync"/> last-error
/// tracking. The Studio shows persistent <c>LastDeploymentError</c> /
/// <c>LastConfigurationError</c> banners on the adapter form; they must clear at the right
/// transitions (Pending, Deployed/Configured) or the user sees stale failure context after
/// fixing a problem and retrying.
/// </summary>
[Collection("CommunicationController")]
public class ErrorTrackingTests(CommunicationControllerFixture fixture)
{
    [Fact]
    public async Task SetAdapterDeploymentStateAsync_Error_RecordsErrorAndTimestamp()
    {
        var tenantId = fixture.TestTenantId;
        var repository = fixture.GetService<ICommunicationRepository>();
        var adapterRtEntityId = await CreateAdapterAsync();

        try
        {
            await repository.SetAdapterDeploymentStateAsync(tenantId, adapterRtEntityId,
                RtDeploymentStateEnum.Error, "helm upgrade failed: foo");

            var adapter = await repository.GetAdapterAsync(tenantId, adapterRtEntityId);
            adapter.LastDeploymentError.Should().Be("helm upgrade failed: foo");
            adapter.LastDeploymentErrorTimestamp.Should().NotBeNull();
        }
        finally
        {
            await DeleteAdapterAsync(adapterRtEntityId);
        }
    }

    [Fact]
    public async Task SetAdapterDeploymentStateAsync_Pending_ClearsPreviousError()
    {
        // Regression test for the UX bug where clicking Deploy after a failure showed the
        // OLD error banner on top of the fresh Pending attempt — the user couldn't tell
        // whether their fix changed anything until either Deployed or a new Error landed.
        // Pending now clears the previous error so the next round-trip starts clean.
        var tenantId = fixture.TestTenantId;
        var repository = fixture.GetService<ICommunicationRepository>();
        var adapterRtEntityId = await CreateAdapterAsync();

        try
        {
            await repository.SetAdapterDeploymentStateAsync(tenantId, adapterRtEntityId,
                RtDeploymentStateEnum.Error, "previous failure");

            await repository.SetAdapterDeploymentStateAsync(tenantId, adapterRtEntityId,
                RtDeploymentStateEnum.Pending);

            var adapter = await repository.GetAdapterAsync(tenantId, adapterRtEntityId);
            adapter.LastDeploymentError.Should().BeNull();
            adapter.LastDeploymentErrorTimestamp.Should().BeNull();
        }
        finally
        {
            await DeleteAdapterAsync(adapterRtEntityId);
        }
    }

    [Fact]
    public async Task SetAdapterDeploymentStateAsync_Deployed_ClearsPreviousError()
    {
        var tenantId = fixture.TestTenantId;
        var repository = fixture.GetService<ICommunicationRepository>();
        var adapterRtEntityId = await CreateAdapterAsync();

        try
        {
            await repository.SetAdapterDeploymentStateAsync(tenantId, adapterRtEntityId,
                RtDeploymentStateEnum.Error, "previous failure");

            await repository.SetAdapterDeploymentStateAsync(tenantId, adapterRtEntityId,
                RtDeploymentStateEnum.Deployed);

            var adapter = await repository.GetAdapterAsync(tenantId, adapterRtEntityId);
            adapter.LastDeploymentError.Should().BeNull();
            adapter.LastDeploymentErrorTimestamp.Should().BeNull();
        }
        finally
        {
            await DeleteAdapterAsync(adapterRtEntityId);
        }
    }

    [Fact]
    public async Task SetAdapterDeploymentStateAsync_Undeployed_PreservesPreviousError()
    {
        // Undeployed is not a user-driven retry — it's typically operator-driven (helm
        // uninstall completed, pool teardown, etc.). Keeping the prior error visible
        // through such transitions matches the original conservative tracking policy.
        var tenantId = fixture.TestTenantId;
        var repository = fixture.GetService<ICommunicationRepository>();
        var adapterRtEntityId = await CreateAdapterAsync();

        try
        {
            await repository.SetAdapterDeploymentStateAsync(tenantId, adapterRtEntityId,
                RtDeploymentStateEnum.Error, "previous failure");

            await repository.SetAdapterDeploymentStateAsync(tenantId, adapterRtEntityId,
                RtDeploymentStateEnum.Undeployed);

            var adapter = await repository.GetAdapterAsync(tenantId, adapterRtEntityId);
            adapter.LastDeploymentError.Should().Be("previous failure");
            adapter.LastDeploymentErrorTimestamp.Should().NotBeNull();
        }
        finally
        {
            await DeleteAdapterAsync(adapterRtEntityId);
        }
    }

    [Fact]
    public async Task SetAdapterConfigurationStateAsync_Pending_ClearsPreviousError()
    {
        // Same UX rationale as the deployment-side test: clicking "Update Configuration"
        // after a failure must wipe the old configuration-error banner.
        var tenantId = fixture.TestTenantId;
        var repository = fixture.GetService<ICommunicationRepository>();
        var adapterRtEntityId = await CreateAdapterAsync();

        try
        {
            await repository.SetAdapterConfigurationStateAsync(tenantId, adapterRtEntityId,
                RtConfigurationStateEnum.Error, "config validation failed");

            await repository.SetAdapterConfigurationStateAsync(tenantId, adapterRtEntityId,
                RtConfigurationStateEnum.Pending, null);

            var adapter = await repository.GetAdapterAsync(tenantId, adapterRtEntityId);
            adapter.LastConfigurationError.Should().BeNull();
            adapter.LastConfigurationErrorTimestamp.Should().BeNull();
        }
        finally
        {
            await DeleteAdapterAsync(adapterRtEntityId);
        }
    }

    [Fact]
    public async Task SetAdapterConfigurationStateAsync_Configured_ClearsPreviousError()
    {
        var tenantId = fixture.TestTenantId;
        var repository = fixture.GetService<ICommunicationRepository>();
        var adapterRtEntityId = await CreateAdapterAsync();

        try
        {
            await repository.SetAdapterConfigurationStateAsync(tenantId, adapterRtEntityId,
                RtConfigurationStateEnum.Error, "config validation failed");

            await repository.SetAdapterConfigurationStateAsync(tenantId, adapterRtEntityId,
                RtConfigurationStateEnum.Configured, null);

            var adapter = await repository.GetAdapterAsync(tenantId, adapterRtEntityId);
            adapter.LastConfigurationError.Should().BeNull();
            adapter.LastConfigurationErrorTimestamp.Should().BeNull();
        }
        finally
        {
            await DeleteAdapterAsync(adapterRtEntityId);
        }
    }

    [Fact]
    public async Task SetAdapterCommunicationStateAsync_Offline_ResetsConfigurationStateToUnconfigured()
    {
        // Invariant: ConfigurationState == Configured implies CommunicationState == Online.
        // A pod that drops offline is no longer running the pushed config, so the UI must
        // not keep showing "Configured" until it comes back.
        var tenantId = fixture.TestTenantId;
        var repository = fixture.GetService<ICommunicationRepository>();
        var adapterRtEntityId = await CreateAdapterAsync();

        try
        {
            await repository.SetAdapterCommunicationStateAsync(tenantId, adapterRtEntityId,
                RtCommunicationStateEnum.Online);
            await repository.SetAdapterConfigurationStateAsync(tenantId, adapterRtEntityId,
                RtConfigurationStateEnum.Configured, null);

            await repository.SetAdapterCommunicationStateAsync(tenantId, adapterRtEntityId,
                RtCommunicationStateEnum.Offline);

            var adapter = await repository.GetAdapterAsync(tenantId, adapterRtEntityId);
            adapter.CommunicationState.Should().Be(RtCommunicationStateEnum.Offline);
            adapter.ConfigurationState.Should().Be(RtConfigurationStateEnum.Unconfigured);
        }
        finally
        {
            await DeleteAdapterAsync(adapterRtEntityId);
        }
    }

    [Fact]
    public async Task SetAdapterCommunicationStateAsync_Offline_PreservesLastConfigurationError()
    {
        // The forced ConfigurationState=Unconfigured on offline must NOT clear the persistent
        // LastConfigurationError — the user still needs to see the prior config-failure
        // context while the adapter is unreachable. The clear branch in
        // ApplyConfigurationErrorTracking only fires on Configured / Pending, so Unconfigured
        // leaves the error intact by design.
        var tenantId = fixture.TestTenantId;
        var repository = fixture.GetService<ICommunicationRepository>();
        var adapterRtEntityId = await CreateAdapterAsync();

        try
        {
            await repository.SetAdapterCommunicationStateAsync(tenantId, adapterRtEntityId,
                RtCommunicationStateEnum.Online);
            await repository.SetAdapterConfigurationStateAsync(tenantId, adapterRtEntityId,
                RtConfigurationStateEnum.Error, "schema mismatch");

            await repository.SetAdapterCommunicationStateAsync(tenantId, adapterRtEntityId,
                RtCommunicationStateEnum.Offline);

            var adapter = await repository.GetAdapterAsync(tenantId, adapterRtEntityId);
            adapter.ConfigurationState.Should().Be(RtConfigurationStateEnum.Unconfigured);
            adapter.LastConfigurationError.Should().Be("schema mismatch");
        }
        finally
        {
            await DeleteAdapterAsync(adapterRtEntityId);
        }
    }

    [Fact]
    public async Task SetAdapterCommunicationStateAsync_Online_LeavesConfigurationStateAlone()
    {
        // Going Online must NOT touch ConfigurationState — the controller decides separately
        // when to (re)push the config. Forcing a value here would clobber an in-flight
        // Pending / a still-valid Configured carry-over from before the adapter blipped.
        var tenantId = fixture.TestTenantId;
        var repository = fixture.GetService<ICommunicationRepository>();
        var adapterRtEntityId = await CreateAdapterAsync();

        try
        {
            await repository.SetAdapterConfigurationStateAsync(tenantId, adapterRtEntityId,
                RtConfigurationStateEnum.Pending, null);

            await repository.SetAdapterCommunicationStateAsync(tenantId, adapterRtEntityId,
                RtCommunicationStateEnum.Online);

            var adapter = await repository.GetAdapterAsync(tenantId, adapterRtEntityId);
            adapter.CommunicationState.Should().Be(RtCommunicationStateEnum.Online);
            adapter.ConfigurationState.Should().Be(RtConfigurationStateEnum.Pending);
        }
        finally
        {
            await DeleteAdapterAsync(adapterRtEntityId);
        }
    }

    [Fact]
    public async Task SetAdapterDeploymentStateAsync_Undeployed_ResetsConfigurationStateToUnconfigured()
    {
        // Invariant: ConfigurationState == Configured implies DeploymentState == Deployed.
        // Helm uninstall removes the pod — the previously-pushed config is gone with it.
        var tenantId = fixture.TestTenantId;
        var repository = fixture.GetService<ICommunicationRepository>();
        var adapterRtEntityId = await CreateAdapterAsync();

        try
        {
            await repository.SetAdapterConfigurationStateAsync(tenantId, adapterRtEntityId,
                RtConfigurationStateEnum.Configured, null);

            await repository.SetAdapterDeploymentStateAsync(tenantId, adapterRtEntityId,
                RtDeploymentStateEnum.Undeployed);

            var adapter = await repository.GetAdapterAsync(tenantId, adapterRtEntityId);
            adapter.DeploymentState.Should().Be(RtDeploymentStateEnum.Undeployed);
            adapter.ConfigurationState.Should().Be(RtConfigurationStateEnum.Unconfigured);
        }
        finally
        {
            await DeleteAdapterAsync(adapterRtEntityId);
        }
    }

    [Fact]
    public async Task SetAdapterDeploymentStateAsync_Error_ResetsConfigurationStateToUnconfigured()
    {
        var tenantId = fixture.TestTenantId;
        var repository = fixture.GetService<ICommunicationRepository>();
        var adapterRtEntityId = await CreateAdapterAsync();

        try
        {
            await repository.SetAdapterConfigurationStateAsync(tenantId, adapterRtEntityId,
                RtConfigurationStateEnum.Configured, null);

            await repository.SetAdapterDeploymentStateAsync(tenantId, adapterRtEntityId,
                RtDeploymentStateEnum.Error, "helm failed");

            var adapter = await repository.GetAdapterAsync(tenantId, adapterRtEntityId);
            adapter.DeploymentState.Should().Be(RtDeploymentStateEnum.Error);
            adapter.ConfigurationState.Should().Be(RtConfigurationStateEnum.Unconfigured);
        }
        finally
        {
            await DeleteAdapterAsync(adapterRtEntityId);
        }
    }

    [Fact]
    public async Task SetAdapterDeploymentStateAsync_Pending_LeavesConfigurationStateAlone()
    {
        // Pending is an in-flight redeploy. The previously-running pod (and its config)
        // might still be there during a helm rolling upgrade, so do NOT force-reset
        // ConfigurationState here — only the terminal Undeployed / Disabled / Error
        // states warrant the reset.
        var tenantId = fixture.TestTenantId;
        var repository = fixture.GetService<ICommunicationRepository>();
        var adapterRtEntityId = await CreateAdapterAsync();

        try
        {
            await repository.SetAdapterConfigurationStateAsync(tenantId, adapterRtEntityId,
                RtConfigurationStateEnum.Configured, null);

            await repository.SetAdapterDeploymentStateAsync(tenantId, adapterRtEntityId,
                RtDeploymentStateEnum.Pending);

            var adapter = await repository.GetAdapterAsync(tenantId, adapterRtEntityId);
            adapter.DeploymentState.Should().Be(RtDeploymentStateEnum.Pending);
            adapter.ConfigurationState.Should().Be(RtConfigurationStateEnum.Configured);
        }
        finally
        {
            await DeleteAdapterAsync(adapterRtEntityId);
        }
    }

    [Fact]
    public async Task DeploymentAndConfigurationErrors_AreTrackedIndependently()
    {
        // The split between LastDeploymentError and LastConfigurationError was introduced
        // so a successful redeploy doesn't mask a still-broken configuration (and vice
        // versa). Verify the two trackers do not interfere.
        var tenantId = fixture.TestTenantId;
        var repository = fixture.GetService<ICommunicationRepository>();
        var adapterRtEntityId = await CreateAdapterAsync();

        try
        {
            await repository.SetAdapterDeploymentStateAsync(tenantId, adapterRtEntityId,
                RtDeploymentStateEnum.Error, "deploy failure");
            await repository.SetAdapterConfigurationStateAsync(tenantId, adapterRtEntityId,
                RtConfigurationStateEnum.Error, "config failure");

            // Successful deploy clears deploy error but must NOT touch config error.
            await repository.SetAdapterDeploymentStateAsync(tenantId, adapterRtEntityId,
                RtDeploymentStateEnum.Deployed);

            var adapter = await repository.GetAdapterAsync(tenantId, adapterRtEntityId);
            adapter.LastDeploymentError.Should().BeNull();
            adapter.LastConfigurationError.Should().Be("config failure");
        }
        finally
        {
            await DeleteAdapterAsync(adapterRtEntityId);
        }
    }

    private async Task<RtEntityId> CreateAdapterAsync()
    {
        var systemContext = fixture.GetSystemContext();
        var tenantRepository = await systemContext.FindTenantRepositoryAsync(fixture.TestTenantId);

        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();

        var adapter = new RtAdapter
        {
            RtId = OctoObjectId.GenerateNewId(),
            CkTypeId = SystemCommunicationCkIds.RtCkAdapterTypeId,
            Name = $"int-test-adapter-{Guid.NewGuid():N}",
            CommunicationState = RtCommunicationStateEnum.Online,
            DeploymentState = RtDeploymentStateEnum.Undeployed,
            ConfigurationState = RtConfigurationStateEnum.Unconfigured
        };

        var operationResult = new OperationResult();
        await tenantRepository.ApplyChangesAsync(session,
            new List<EntityUpdateInfo<RtAdapter>> { EntityUpdateInfo<RtAdapter>.CreateInsert(adapter) },
            operationResult);

        if (operationResult.HasErrors || operationResult.HasFatalErrors)
        {
            await session.AbortTransactionAsync();
            throw new InvalidOperationException(
                $"Failed to insert test adapter: {operationResult.GetMessages()}");
        }

        await session.CommitTransactionAsync();
        return new RtEntityId(SystemCommunicationCkIds.RtCkAdapterTypeId, adapter.RtId);
    }

    private async Task DeleteAdapterAsync(RtEntityId adapterRtEntityId)
    {
        var systemContext = fixture.GetSystemContext();
        var tenantRepository = await systemContext.FindTenantRepositoryAsync(fixture.TestTenantId);

        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();

        try
        {
            var operationResult = new OperationResult();
            await tenantRepository.ApplyChangesAsync(session,
                new List<EntityUpdateInfo<RtAdapter>>
                {
                    EntityUpdateInfo<RtAdapter>.CreateDelete(adapterRtEntityId)
                },
                operationResult);

            await session.CommitTransactionAsync();
        }
        catch
        {
            await session.AbortTransactionAsync();
            // Cleanup is best-effort.
        }
    }
}
