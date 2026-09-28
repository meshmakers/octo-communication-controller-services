using FluentAssertions;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Adapters;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Hubs;
using Meshmakers.Octo.Backend.CommunicationControllerServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Options;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.IntegrationTests.Repository;

/// <summary>
///     AB#5256 — the two pool attributes the idle shrink decides on, against real MongoDB.
/// </summary>
/// <remarks>
///     <para>
///         🔴 <b>Why this cannot be a unit test.</b> <c>MinReplicas</c> is declared on
///         <c>AdapterPool</c>, but <c>IdleTimeoutMinutes</c> is <b>inherited from
///         <c>DeployableWorkload</c></b> and, until this change, was never read for a pool by anything —
///         the idle watchdog skips pools by design. A substituted repository returns whatever the test
///         put on an in-memory entity, so it would confirm the reading no matter what storage does. What
///         has to be proven on a real database is that the inherited attribute survives the write/read
///         round trip of a <c>RtAdapterPool</c> through the <b>polymorphic</b> workload collection, and
///         that the model default materialises when nothing ever set it.
///     </para>
///     <para>
///         🔴 The default is the load-bearing half. A pool seeded by a blueprint declares a replica range
///         and a scaling policy and says nothing about <c>IdleTimeoutMinutes</c> — every pool in the
///         estate is that shape today. If the attribute came back as <c>0</c> rather than as the model's
///         declared <c>30</c>, the shrink would fall onto its one-minute floor and every pool in the
///         estate would give up a member a minute after its last lease. That is not a reading a mock can
///         be wrong about in a way anyone would notice.
///     </para>
/// </remarks>
[Collection("CommunicationController")]
public class AdapterPoolIdleShrinkInputsTests(CommunicationControllerFixture fixture)
{
    /// <summary>
    ///     The values the scheduler reads come back exactly as they were declared — through
    ///     <see cref="ICommunicationRepository.GetWorkloadByRtIdAsync" />, which is the call the
    ///     scheduling round really makes, and which returns the polymorphic base type.
    /// </summary>
    [Fact]
    public async Task AnExplicitIdleTimeout_SurvivesTheRoundTripOnAPool()
    {
        var repository = fixture.GetService<ICommunicationRepository>();
        var pool = await CreateAdapterPoolAsync(minReplicas: 2, maxReplicas: 5, idleTimeoutMinutes: 7);

        try
        {
            var loaded = await repository.GetWorkloadByRtIdAsync(fixture.TestTenantId, pool.RtId);

            // The scheduler casts the result of this very call to RtAdapterPool and reads the three
            // numbers below off it; a pool that came back as a plain workload would silently disable
            // the shrink instead of failing.
            loaded.Should().BeOfType<RtAdapterPool>();
            var reloaded = (RtAdapterPool)loaded!;
            reloaded.IdleTimeoutMinutes.Should().Be(7);
            reloaded.MinReplicas.Should().Be(2);
            reloaded.MaxReplicas.Should().Be(5);
        }
        finally
        {
            await DeleteAsync(pool);
        }
    }

    /// <summary>
    ///     A pool written the way a blueprint seeds one — a replica range and a policy, no idle timeout —
    ///     reads back as the model's declared thirty minutes, and that is the window the scheduler uses.
    /// </summary>
    [Fact]
    public async Task APoolThatNeverDeclaredAnIdleTimeout_ReadsBackAsTheModelDefault()
    {
        var repository = fixture.GetService<ICommunicationRepository>();
        var pool = await CreateAdapterPoolAsync(minReplicas: 1, maxReplicas: 2, idleTimeoutMinutes: null);

        try
        {
            var reloaded = (RtAdapterPool)(await repository.GetWorkloadByRtIdAsync(fixture.TestTenantId, pool.RtId))!;

            // 30 is what attributes/helmDeployment.yaml declares, and 0 is what a missing value would
            // look like.
            reloaded.IdleTimeoutMinutes.Should().Be(30);

            // And the window the scheduler derives from it, through the same method the round calls: half
            // an hour, not the one-minute floor a zero would collapse to.
            ArrangeScheduler().ResolveIdleShrinkWindow(reloaded).Should().Be(TimeSpan.FromMinutes(30));
        }
        finally
        {
            await DeleteAsync(pool);
        }
    }

    /// <summary>
    ///     A <see cref="LeaseSchedulerService" /> built only far enough to answer the window question:
    ///     everything the decision reads is the pool entity and the controller options.
    /// </summary>
    private static LeaseSchedulerService ArrangeScheduler()
    {
        return new LeaseSchedulerService(Substitute.For<IAdapterCache>(),
            Substitute.For<ICommunicationRepository>(),
            Substitute.For<IAdapterPoolConnectionManager>(),
            Substitute.For<ICommunicationEventService>(),
            Substitute.For<ILeaseService>(),
            Substitute.For<IDeploymentSiteService>(),
            new OptionsWrapper<CommunicationControllerOptions>(new CommunicationControllerOptions()));
    }

    private async Task<RtAdapterPool> CreateAdapterPoolAsync(int minReplicas, int maxReplicas,
        int? idleTimeoutMinutes)
    {
        var systemContext = fixture.GetSystemContext();
        var tenantRepository = await systemContext.FindTenantRepositoryAsync(fixture.TestTenantId);

        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();

        var pool = new RtAdapterPool
        {
            RtId = OctoObjectId.GenerateNewId(),
            CkTypeId = SystemCommunicationCkIds.RtCkAdapterPoolTypeId,
            Name = $"int-test-idle-shrink-pool-{Guid.NewGuid():N}",
            DeploymentState = RtDeploymentStateEnum.Deployed,
            MinReplicas = minReplicas,
            MaxReplicas = maxReplicas,
            ScaleUpPolicy = RtPoolScaleUpPolicyEnum.QueueDepthOrWaitSeconds,
            ScaleUpQueueDepthThreshold = 0,
            ScaleUpQueueWaitSeconds = 0,
            SharingMode = RtAdapterSharingModeEnum.Descendants
        };

        // Left untouched for the default case: assigning the property would write a value and prove
        // nothing about what a seeded pool carries.
        if (idleTimeoutMinutes is { } minutes)
        {
            pool.IdleTimeoutMinutes = minutes;
        }

        var operationResult = new OperationResult();
        await tenantRepository.ApplyChangesAsync(session,
            new List<EntityUpdateInfo<RtAdapterPool>> { EntityUpdateInfo<RtAdapterPool>.CreateInsert(pool) },
            operationResult);

        if (operationResult.HasErrors || operationResult.HasFatalErrors)
        {
            await session.AbortTransactionAsync();
            throw new InvalidOperationException($"Failed to insert test adapter pool: {operationResult.GetMessages()}");
        }

        await session.CommitTransactionAsync();
        return pool;
    }

    private async Task DeleteAsync(RtAdapterPool pool)
    {
        var systemContext = fixture.GetSystemContext();
        var tenantRepository = await systemContext.FindTenantRepositoryAsync(fixture.TestTenantId);

        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();

        try
        {
            var operationResult = new OperationResult();
            await tenantRepository.ApplyChangesAsync(session,
                new List<EntityUpdateInfo<RtAdapterPool>>
                {
                    EntityUpdateInfo<RtAdapterPool>.CreateDelete(pool.ToRtEntityId())
                },
                operationResult);
            await session.CommitTransactionAsync();
        }
        catch
        {
            await session.AbortTransactionAsync();
        }
    }
}
