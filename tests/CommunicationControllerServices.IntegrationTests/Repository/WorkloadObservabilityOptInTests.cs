using FluentAssertions;
using Meshmakers.Octo.Backend.CommunicationControllerServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Xunit;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.IntegrationTests.Repository;

/// <summary>
/// AB#5432: the per-tenant opt-in behind the workload state metrics. The read is untyped on purpose —
/// <c>PublishWorkloadObservability</c> is new in <c>System-2.3.0</c> while this service compiles
/// against the published <c>System</c> package, which lags by a train — so the thing that has to be
/// verified against a real database is that the untyped read finds the attribute when it is there and
/// defaults to <c>false</c> in every other case, rather than throwing and taking the sweep with it.
/// </summary>
[Collection("CommunicationController")]
public class WorkloadObservabilityOptInTests(CommunicationControllerFixture fixture)
{
    private const string AttributeName = "PublishWorkloadObservability";

    /// <summary>
    /// A tenant whose CK model predates the attribute, or that simply never set it. The absence must
    /// read as "not opted in", not as an error — this is the state of almost every tenant.
    /// </summary>
    [Fact]
    public async Task IsWorkloadObservabilityEnabledAsync_AttributeAbsent_IsFalse()
    {
        var repository = fixture.GetService<ICommunicationRepository>();
        await ReplaceTenantModeConfigurationAsync(optIn: null);

        var enabled = await repository.IsWorkloadObservabilityEnabledAsync(fixture.TestTenantId);

        enabled.Should().BeFalse();
    }

    [Fact]
    public async Task IsWorkloadObservabilityEnabledAsync_AttributeFalse_IsFalse()
    {
        var repository = fixture.GetService<ICommunicationRepository>();
        await ReplaceTenantModeConfigurationAsync(optIn: false);

        var enabled = await repository.IsWorkloadObservabilityEnabledAsync(fixture.TestTenantId);

        enabled.Should().BeFalse();
    }

    /// <summary>
    /// The one case that cannot be asserted by a unit test: the attribute name this service hard-codes
    /// really is the name the value round-trips under in MongoDB.
    /// </summary>
    [Fact]
    public async Task IsWorkloadObservabilityEnabledAsync_AttributeTrue_IsTrue()
    {
        var repository = fixture.GetService<ICommunicationRepository>();
        await ReplaceTenantModeConfigurationAsync(optIn: true);

        var enabled = await repository.IsWorkloadObservabilityEnabledAsync(fixture.TestTenantId);

        enabled.Should().BeTrue();
    }

    /// <summary>
    /// Leaves the tenant with exactly one <c>TenantModeConfiguration</c>, carrying the given opt-in
    /// (<c>null</c> = attribute not written at all). Written with
    /// <c>SetAttributeRawValue</c> rather than through the generated property because the generated
    /// class of the published <c>System</c> package does not know the attribute yet.
    /// </summary>
    private async Task ReplaceTenantModeConfigurationAsync(bool? optIn)
    {
        var systemContext = fixture.GetSystemContext();
        var tenantRepository = await systemContext.FindTenantRepositoryAsync(fixture.TestTenantId);

        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();

        var existing = await tenantRepository.GetRtEntitiesByTypeAsync(session,
            SystemCkIds.RtCkTenantModeConfigurationTypeId, RtEntityQueryOptions.Create());
        foreach (var entity in existing.Items)
        {
            await tenantRepository.DeleteOneRtEntityByRtIdAsync(session,
                SystemCkIds.RtCkTenantModeConfigurationTypeId, entity.RtId, DeleteOptions.Erase);
        }

        var configuration = await tenantRepository.CreateTransientRtEntityAsync<RtTenantModeConfiguration>();
        configuration.RtId = OctoObjectId.GenerateNewId();
        if (optIn.HasValue)
        {
            configuration.SetAttributeRawValue(AttributeName, optIn.Value);
        }

        await tenantRepository.InsertOneRtEntityAsync(session, configuration);
        await session.CommitTransactionAsync();
    }
}
