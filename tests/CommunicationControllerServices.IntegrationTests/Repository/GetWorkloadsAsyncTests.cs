using FluentAssertions;
using Meshmakers.Octo.Backend.CommunicationControllerServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v3;
using Xunit;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.IntegrationTests.Repository;

/// <summary>
/// <c>GetWorkloadsAsync</c> feeds the Communication disable guard (AB#4255): it must see every
/// Adapter and Application of the tenant as its concrete type, whether or not a pool manages it.
/// </summary>
[Collection("CommunicationController")]
public class GetWorkloadsAsyncTests(CommunicationControllerFixture fixture)
{
    [Fact]
    public async Task GetWorkloadsAsync_ReturnsAdaptersAndApplications_WithTheirPersistedDeploymentState()
    {
        var repository = fixture.GetService<ICommunicationRepository>();
        var systemContext = fixture.GetSystemContext();
        var tenantRepository = await systemContext.FindTenantRepositoryAsync(fixture.TestTenantId);

        var adapterRtId = OctoObjectId.GenerateNewId();
        var applicationRtId = OctoObjectId.GenerateNewId();
        var suffix = Guid.NewGuid().ToString("N");

        using (var session = await tenantRepository.GetSessionAsync())
        {
            session.StartTransaction();
            var adapter = await tenantRepository.CreateTransientRtEntityAsync<RtAdapter>();
            adapter.RtId = adapterRtId;
            adapter.Name = $"guard-adapter-{suffix}";
            adapter.DeploymentState = RtDeploymentStateEnum.Deployed;
            await tenantRepository.InsertOneRtEntityAsync(session, adapter);

            var application = await tenantRepository.CreateTransientRtEntityAsync<RtApplication>();
            application.RtId = applicationRtId;
            application.Name = $"guard-application-{suffix}";
            application.DeploymentState = RtDeploymentStateEnum.Error;
            await tenantRepository.InsertOneRtEntityAsync(session, application);
            await session.CommitTransactionAsync();
        }

        var workloads = await repository.GetWorkloadsAsync(fixture.TestTenantId);

        // The shared test tenant may hold other workloads; only ours are asserted.
        var adapterLoaded = workloads.Single(w => w.RtId == adapterRtId);
        adapterLoaded.Should().BeOfType<RtAdapter>();
        adapterLoaded.DeploymentState.Should().Be(RtDeploymentStateEnum.Deployed);
        adapterLoaded.Name.Should().Be($"guard-adapter-{suffix}");

        var applicationLoaded = workloads.Single(w => w.RtId == applicationRtId);
        applicationLoaded.Should().BeOfType<RtApplication>();
        applicationLoaded.DeploymentState.Should().Be(RtDeploymentStateEnum.Error);
    }
}
