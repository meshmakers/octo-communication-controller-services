using Meshmakers.Octo.Backend.CommunicationControllerServices.Models;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using Microsoft.Extensions.Logging;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;

/// <summary>
/// Repository for pool related operations
/// </summary>
internal class CommunicationRepository : ICommunicationRepository
{
    private readonly ISystemContext _systemContext;
    private readonly ILogger<CommunicationRepository> _logger;

    /// <summary>
    /// Constructor
    /// </summary>
    /// <param name="systemContext">The root object of the persistence layer</param>
    /// <param name="logger">Logger instance</param>
    public CommunicationRepository(ISystemContext systemContext, ILogger<CommunicationRepository> logger)
    {
        _systemContext = systemContext;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyCollection<RtAdapter>> GetAdaptersAsync(string tenantId, OctoObjectId adapterPoolRtId)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        using var session = await tenantRepository.GetSessionAsync();
        try
        {
            var resultSet = await tenantRepository.GetRtAssociationTargetsAsync<RtDeploymentSite, RtAdapter>(session,
                [adapterPoolRtId] , SystemCommunicationCkIds.RtCkHostsRoleId,
                GraphDirections.Inbound, null, RtEntityQueryOptions.Create());

            if (!resultSet.Any())
            {
                throw CommunicationRepositoryException.DeploymentSiteNotFound(tenantId, adapterPoolRtId);
            }

            return resultSet.First().Value.Items.ToList();
        }
        catch (Exception e)
        {
            throw CommunicationRepositoryException.CommonFailedGettingAdapters(tenantId, adapterPoolRtId, e);
        }
    }


    /// <inheritdoc />
    public async Task<IReadOnlyCollection<RtDeployableWorkload>> GetWorkloadsForDeploymentSiteAsync(string tenantId,
        OctoObjectId adapterPoolRtId)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        using var session = await tenantRepository.GetSessionAsync();
        try
        {
            // RtDeployableWorkload is abstract — the runtime engine returns the
            // concrete RtAdapter / RtApplication instances polymorphically.
            var resultSet = await tenantRepository.GetRtAssociationTargetsAsync<RtDeploymentSite, RtDeployableWorkload>(session,
                [adapterPoolRtId], SystemCommunicationCkIds.RtCkHostsRoleId,
                GraphDirections.Inbound, null, RtEntityQueryOptions.Create());

            if (!resultSet.Any())
            {
                return Array.Empty<RtDeployableWorkload>();
            }

            return resultSet.First().Value.Items.ToList();
        }
        catch (Exception e)
        {
            throw CommunicationRepositoryException.CommonFailedGettingAdapters(tenantId, adapterPoolRtId, e);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyCollection<RtDeployableWorkload>> GetWorkloadsAsync(string tenantId)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        using var session = await tenantRepository.GetSessionAsync();
        try
        {
            // RtDeployableWorkload is abstract — the runtime engine returns the
            // concrete RtAdapter / RtApplication instances polymorphically.
            var resultSet = await tenantRepository.GetRtEntitiesByTypeAsync<RtDeployableWorkload>(session,
                RtEntityQueryOptions.Create());

            return resultSet.Items.ToList();
        }
        catch (Exception e)
        {
            throw CommunicationRepositoryException.CommonFailedGettingWorkloads(tenantId, e);
        }
    }

    /// <inheritdoc />
    public async Task<RtDeployableWorkload?> GetWorkloadByRtIdAsync(string tenantId, OctoObjectId workloadRtId)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        using var session = await tenantRepository.GetSessionAsync();
        try
        {
            // RtDeployableWorkload is abstract; the runtime engine returns
            // the concrete RtAdapter / RtApplication.
            return await tenantRepository.GetRtEntityByRtIdAsync<RtDeployableWorkload>(session, workloadRtId);
        }
        catch (Exception e)
        {
            throw CommunicationRepositoryException.CommonFailedGettingAdapters(tenantId, workloadRtId, e);
        }
    }

    /// <inheritdoc />
    public async Task SetWorkloadLifecycleStateAsync(string tenantId, OctoObjectId workloadRtId,
        RtLifecycleStateEnum lifecycleState, string? statusMessage = null)
    {
        await UpdateWorkloadPolymorphicAsync(tenantId, workloadRtId,
            () => new RtAdapter { LifecycleState = lifecycleState, StatusMessage = statusMessage },
            () => new RtApplication { LifecycleState = lifecycleState, StatusMessage = statusMessage },
            () => new RtAdapterPool { LifecycleState = lifecycleState, StatusMessage = statusMessage },
            e => CommunicationRepositoryException.CommonFailedSetWorkloadLifecycleState(tenantId, workloadRtId,
                lifecycleState, e));
    }

    /// <inheritdoc />
    public async Task SetWorkloadLastActivityAsync(string tenantId, OctoObjectId workloadRtId,
        DateTime lastActivityAtUtc)
    {
        await UpdateWorkloadPolymorphicAsync(tenantId, workloadRtId,
            () => new RtAdapter { LastActivityAt = lastActivityAtUtc },
            () => new RtApplication { LastActivityAt = lastActivityAtUtc },
            () => new RtAdapterPool { LastActivityAt = lastActivityAtUtc },
            e => CommunicationRepositoryException.CommonFailedSetWorkloadLastActivity(tenantId, workloadRtId, e));
    }

    /// <inheritdoc />
    public async Task SetWorkloadOnDemandCapabilityAsync(string tenantId, OctoObjectId workloadRtId,
        bool onDemandCapable, string? blockingReasons)
    {
        await UpdateWorkloadPolymorphicAsync(tenantId, workloadRtId,
            () => new RtAdapter { OnDemandCapable = onDemandCapable, OnDemandBlockingReasons = blockingReasons },
            () => new RtApplication { OnDemandCapable = onDemandCapable, OnDemandBlockingReasons = blockingReasons },
            () => new RtAdapterPool { OnDemandCapable = onDemandCapable, OnDemandBlockingReasons = blockingReasons },
            e => CommunicationRepositoryException.CommonFailedSetWorkloadOnDemandCapability(tenantId, workloadRtId, e));
    }

    /// <summary>
    /// Shared load-then-update helper for the AB#4914 lifecycle writers.
    /// <c>RtDeployableWorkload</c> is abstract, and an <c>EntityUpdateInfo</c>
    /// needs the concrete CK type — so the entity is loaded first and the
    /// matching partial-update DTO is applied.
    /// </summary>
    private async Task UpdateWorkloadPolymorphicAsync(string tenantId, OctoObjectId workloadRtId,
        Func<RtAdapter> adapterUpdateFactory, Func<RtApplication> applicationUpdateFactory,
        Func<RtAdapterPool> adapterPoolUpdateFactory,
        Func<Exception, Exception> exceptionFactory)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        using var session = await tenantRepository.GetSessionAsync();
        try
        {
            session.StartTransaction();

            var workload = await tenantRepository.GetRtEntityByRtIdAsync<RtDeployableWorkload>(session, workloadRtId);
            if (workload == null)
            {
                throw CommunicationRepositoryException.WorkloadNotFound(tenantId, workloadRtId);
            }

            OperationResult operationResult = new();
            switch (workload)
            {
                case RtAdapter:
                    {
                        var rtEntityId = new RtEntityId(SystemCommunicationCkIds.RtCkAdapterTypeId, workloadRtId);
                        await tenantRepository.ApplyChangesAsync(session,
                            [EntityUpdateInfo<RtAdapter>.CreateUpdate(rtEntityId, adapterUpdateFactory())],
                            operationResult);
                        break;
                    }
                case RtApplication:
                    {
                        var rtEntityId = new RtEntityId(SystemCommunicationCkIds.RtCkApplicationTypeId, workloadRtId);
                        await tenantRepository.ApplyChangesAsync(session,
                            [EntityUpdateInfo<RtApplication>.CreateUpdate(rtEntityId, applicationUpdateFactory())],
                            operationResult);
                        break;
                    }
                // AB#4924: an AdapterPool is a DeployableWorkload too, and it was falling into the
                // default arm below — every lifecycle write against a pool threw WorkloadNotFound,
                // which is the least informative way possible to say "this type is not handled".
                case RtAdapterPool:
                    {
                        var rtEntityId = new RtEntityId(SystemCommunicationCkIds.RtCkAdapterPoolTypeId, workloadRtId);
                        await tenantRepository.ApplyChangesAsync(session,
                            [EntityUpdateInfo<RtAdapterPool>.CreateUpdate(rtEntityId, adapterPoolUpdateFactory())],
                            operationResult);
                        break;
                    }
                default:
                    throw CommunicationRepositoryException.WorkloadNotFound(tenantId, workloadRtId);
            }

            if (operationResult.HasErrors || operationResult.HasFatalErrors)
            {
                throw CommunicationRepositoryException.CommonOperationFailed(operationResult);
            }

            await session.CommitTransactionAsync();
        }
        catch (CommunicationRepositoryException)
        {
            throw;
        }
        catch (Exception e)
        {
            throw exceptionFactory(e);
        }
    }

    /// <inheritdoc />
    /// <inheritdoc />
    public async Task<LendingScope?> TryGetAdapterPoolLendingScopeAsync(string lenderTenantId, string adapterPoolRtId)
    {
        // AB#4924 — reads an AdapterPool in a DIFFERENT tenant than the caller's. That is the
        // whole point: the borrower's Adapter carries LentFromTenantId / LentFromAdapterPoolRtId as plain
        // values because a CK association cannot cross a tenant database, so the controller is the
        // only component that can resolve the reference, and it does it here.
        //
        // Returns null for every "cannot resolve" case rather than throwing, because the caller
        // treats an unresolvable pool and a pool that does not lend here identically: both are a
        // refused deploy with a named reason (concept §6, "Parent tenant deleted while lending").
        if (!OctoObjectId.TryParse(adapterPoolRtId, out var rtId))
        {
            _logger.LogWarning("[{LenderTenantId}] LentFromAdapterPoolRtId '{AdapterPoolRtId}' is not a valid RtId",
                lenderTenantId, adapterPoolRtId);
            return null;
        }

        var tenantRepository = await _systemContext.TryFindTenantRepositoryAsync(lenderTenantId);
        if (tenantRepository is null)
        {
            _logger.LogWarning("Lending tenant '{LenderTenantId}' cannot be resolved", lenderTenantId);
            return null;
        }

        using var session = await tenantRepository.GetSessionAsync();
        try
        {
            var pool = await tenantRepository.GetRtEntityByRtIdAsync<RtAdapterPool>(session, rtId);
            if (pool is null)
            {
                return null;
            }

            // Materialise the attribute value list into a plain collection before it leaves the
            // session: IAttributeValueList is a live view over the entity, and the resolver
            // caches what it is handed.
            return new LendingScope((int)pool.SharingMode, pool.LendingAllowedTenantIds?.ToList());
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "[{LenderTenantId}] Failed to read adapter pool '{AdapterPoolRtId}'",
                lenderTenantId, adapterPoolRtId);
            return null;
        }
    }

    public async Task<RtDeploymentSite?> GetDeploymentSiteForWorkloadAsync(string tenantId, OctoObjectId workloadRtId)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        using var session = await tenantRepository.GetSessionAsync();
        try
        {
            // Workload's outbound HostedBy association points at the deployment site it runs at.
            var resultSet = await tenantRepository
                .GetRtAssociationTargetsAsync<RtDeployableWorkload, RtDeploymentSite>(session,
                    [workloadRtId], SystemCommunicationCkIds.RtCkHostsRoleId,
                    GraphDirections.Outbound, null, RtEntityQueryOptions.Create());

            if (!resultSet.Any())
            {
                return null;
            }

            return resultSet.First().Value.Items.FirstOrDefault();
        }
        catch (Exception e)
        {
            throw CommunicationRepositoryException.CommonFailedGettingAdapters(tenantId, workloadRtId, e);
        }
    }

    /// <inheritdoc />
    public async Task<RtHelmRepositoryConfiguration?> GetHelmRepositoryForWorkloadAsync(string tenantId,
        OctoObjectId workloadRtId)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        using var session = await tenantRepository.GetSessionAsync();
        try
        {
            var resultSet = await tenantRepository
                .GetRtAssociationTargetsAsync<RtDeployableWorkload, RtHelmRepositoryConfiguration>(session,
                    [workloadRtId], SystemCommunicationCkIds.RtCkHelmRepositoryRoleId,
                    GraphDirections.Outbound, null, RtEntityQueryOptions.Create());

            if (!resultSet.Any())
            {
                return null;
            }

            return resultSet.First().Value.Items.FirstOrDefault();
        }
        catch (Exception e)
        {
            throw CommunicationRepositoryException.CommonFailedGettingAdapters(tenantId, workloadRtId, e);
        }
    }

    /// <inheritdoc />
    public async Task<RtServiceAccountConfiguration?> GetServiceAccountForAdapterAsync(string tenantId,
        OctoObjectId adapterRtId)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        using var session = await tenantRepository.GetSessionAsync();
        try
        {
            // Dedicated PipelineServiceAccount role (AB#5027), NOT the generic Uses role — see
            // ConstructionKit/associations/pipelineServiceAccount.yaml for why reusing Uses
            // would break SystemConfigurationInterface in the generated GraphQL schema.
            var resultSet = await tenantRepository
                .GetRtAssociationTargetsAsync<RtAdapter, RtServiceAccountConfiguration>(session,
                    [adapterRtId], SystemCommunicationCkIds.RtCkPipelineServiceAccountRoleId,
                    GraphDirections.Outbound, null, RtEntityQueryOptions.Create());

            if (!resultSet.Any())
            {
                return null;
            }

            return resultSet.First().Value.Items.FirstOrDefault();
        }
        catch (Exception e)
        {
            throw CommunicationRepositoryException.CommonFailedGettingServiceAccountForAdapter(tenantId, adapterRtId, e);
        }
    }

    /// <inheritdoc />
    public async Task<RtServiceAccountConfiguration?> GetServiceAccountByWellKnownNameAsync(string tenantId,
        string wellKnownName)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        using var session = await tenantRepository.GetSessionAsync();
        try
        {
            var queryOptions = RtEntityQueryOptions.Create()
                .FieldFilter(nameof(RtServiceAccountConfiguration.RtWellKnownName), FieldFilterOperator.Equals,
                    wellKnownName);

            var resultSet = await tenantRepository
                .GetRtEntitiesByTypeAsync<RtServiceAccountConfiguration>(session, queryOptions, take: 1);

            return resultSet.Items.FirstOrDefault();
        }
        catch (Exception e)
        {
            throw CommunicationRepositoryException.CommonFailedGettingServiceAccountByWellKnownName(tenantId,
                wellKnownName, e);
        }
    }

    /// <inheritdoc />
    public async Task<RtServiceAccountConfiguration?> GetServiceAccountByRtIdAsync(string tenantId,
        OctoObjectId serviceAccountRtId)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        using var session = await tenantRepository.GetSessionAsync();
        try
        {
            return await tenantRepository.GetRtEntityByRtIdAsync<RtServiceAccountConfiguration>(session,
                serviceAccountRtId);
        }
        catch (Exception e)
        {
            throw CommunicationRepositoryException.CommonFailedGettingServiceAccountByRtId(tenantId,
                serviceAccountRtId, e);
        }
    }

    /// <inheritdoc />
    public async Task<RtAdapter?> GetAdapterForServiceAccountAsync(string tenantId, OctoObjectId serviceAccountRtId)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        using var session = await tenantRepository.GetSessionAsync();
        try
        {
            // Inbound over the dedicated PipelineServiceAccount role: the edge lives on the
            // adapter, so from the configuration's side it is the incoming direction — same
            // pattern as GetAdaptersAsync resolves DeploymentSite→Adapter over the Hosts role.
            var resultSet = await tenantRepository
                .GetRtAssociationTargetsAsync<RtServiceAccountConfiguration, RtAdapter>(session,
                    [serviceAccountRtId], SystemCommunicationCkIds.RtCkPipelineServiceAccountRoleId,
                    GraphDirections.Inbound, null, RtEntityQueryOptions.Create());

            if (!resultSet.Any())
            {
                return null;
            }

            return resultSet.First().Value.Items.FirstOrDefault();
        }
        catch (Exception e)
        {
            throw CommunicationRepositoryException.CommonFailedGettingAdapterForServiceAccount(tenantId,
                serviceAccountRtId, e);
        }
    }

    /// <inheritdoc />
    public async Task UpdateServiceAccountAsync(string tenantId, RtServiceAccountConfiguration serviceAccount)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        using var session = await tenantRepository.GetSessionAsync();
        try
        {
            session.StartTransaction();

            var entityUpdateInfoList = new List<EntityUpdateInfo<RtServiceAccountConfiguration>>
            {
                EntityUpdateInfo<RtServiceAccountConfiguration>.CreateUpdate(serviceAccount.ToRtEntityId(),
                    serviceAccount)
            };

            OperationResult operationResult = new();
            await tenantRepository.ApplyChangesAsync(session, entityUpdateInfoList, operationResult);
            if (operationResult.HasErrors || operationResult.HasFatalErrors)
            {
                throw CommunicationRepositoryException.CommonOperationFailed(operationResult);
            }

            await session.CommitTransactionAsync();
        }
        catch (CommunicationRepositoryException)
        {
            throw;
        }
        catch (Exception e)
        {
            // 🔴 serviceAccount carries a plaintext secret — only its rtId goes into the message.
            throw CommunicationRepositoryException.CommonFailedUpdatingServiceAccount(tenantId,
                serviceAccount.RtId, e);
        }
    }

    /// <inheritdoc />
    public async Task SavePipelineServiceAccountAsync(string tenantId, RtEntityId adapterRtEntityId,
        RtServiceAccountConfiguration serviceAccount, bool isNewEntity)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        using var session = await tenantRepository.GetSessionAsync();
        try
        {
            session.StartTransaction();

            var entityUpdateInfoList = new List<EntityUpdateInfo<RtServiceAccountConfiguration>>
            {
                isNewEntity
                    ? EntityUpdateInfo<RtServiceAccountConfiguration>.CreateInsert(serviceAccount)
                    : EntityUpdateInfo<RtServiceAccountConfiguration>.CreateUpdate(serviceAccount.ToRtEntityId(),
                        serviceAccount)
            };

            OperationResult operationResult = new();
            await tenantRepository.ApplyChangesAsync(session, entityUpdateInfoList, operationResult);
            if (operationResult.HasErrors || operationResult.HasFatalErrors)
            {
                throw CommunicationRepositoryException.CommonOperationFailed(operationResult);
            }

            // The edge is written in the same transaction as the entity, so a failure can never
            // leave a credential entity nobody points at. Read the current edge first: the
            // outbound multiplicity is ZeroOrOne, so blindly inserting a second one is rejected
            // by the engine, and re-inserting the identical one would be a duplicate.
            var existingTargets = await tenantRepository
                .GetRtAssociationTargetsAsync<RtAdapter, RtServiceAccountConfiguration>(session,
                    [adapterRtEntityId.RtId], SystemCommunicationCkIds.RtCkPipelineServiceAccountRoleId,
                    GraphDirections.Outbound, null, RtEntityQueryOptions.Create());

            var currentTarget = existingTargets.FirstOrDefault().Value?.Items.FirstOrDefault();
            if (currentTarget != null && currentTarget.RtId == serviceAccount.RtId)
            {
                await session.CommitTransactionAsync();
                return;
            }

            var associations = new List<AssociationUpdateInfo>();
            if (currentTarget != null)
            {
                // Points at a different account — replace it. An adapter has exactly one default.
                associations.Add(AssociationUpdateInfo.CreateDelete(adapterRtEntityId,
                    currentTarget.ToRtEntityId(), SystemCommunicationCkIds.RtCkPipelineServiceAccountRoleId));
            }

            associations.Add(AssociationUpdateInfo.CreateInsert(adapterRtEntityId,
                serviceAccount.ToRtEntityId(), SystemCommunicationCkIds.RtCkPipelineServiceAccountRoleId));

            await tenantRepository.ApplyChangesAsync(session, associations, operationResult);
            if (operationResult.HasErrors || operationResult.HasFatalErrors)
            {
                throw CommunicationRepositoryException.CommonOperationFailed(operationResult);
            }

            await session.CommitTransactionAsync();
        }
        catch (CommunicationRepositoryException)
        {
            throw;
        }
        catch (Exception e)
        {
            // 🔴 serviceAccount carries a plaintext secret — only its rtId goes into the message.
            throw CommunicationRepositoryException.CommonFailedSavingPipelineServiceAccount(tenantId,
                adapterRtEntityId, serviceAccount.RtId, e);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyCollection<RtSignalChannel>> GetSignalChannelsAsync(string tenantId)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        using var session = await tenantRepository.GetSessionAsync();
        try
        {
            var resultSet = await tenantRepository.GetRtEntitiesByTypeAsync<RtSignalChannel>(session,
                RtEntityQueryOptions.Create());

            return resultSet.Items.ToList();
        }
        catch (Exception e)
        {
            throw CommunicationRepositoryException.CommonFailedGettingSignalChannels(tenantId, e);
        }
    }

    /// <inheritdoc />
    public async Task SaveSignalChannelAsync(string tenantId, RtSignalChannel signalChannel, bool isNewEntity)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        using var session = await tenantRepository.GetSessionAsync();
        try
        {
            session.StartTransaction();

            var entityUpdateInfoList = new List<EntityUpdateInfo<RtSignalChannel>>
            {
                isNewEntity
                    ? EntityUpdateInfo<RtSignalChannel>.CreateInsert(signalChannel)
                    : EntityUpdateInfo<RtSignalChannel>.CreateUpdate(signalChannel.ToRtEntityId(), signalChannel)
            };

            OperationResult operationResult = new();
            await tenantRepository.ApplyChangesAsync(session, entityUpdateInfoList, operationResult);
            if (operationResult.HasErrors || operationResult.HasFatalErrors)
            {
                throw CommunicationRepositoryException.CommonOperationFailed(operationResult);
            }

            await session.CommitTransactionAsync();
        }
        catch (CommunicationRepositoryException)
        {
            throw;
        }
        catch (Exception e)
        {
            throw CommunicationRepositoryException.CommonFailedSavingSignalChannel(tenantId, signalChannel.RtId, e);
        }
    }

    /// <inheritdoc />
    public async Task DeleteSignalChannelAsync(string tenantId, RtEntityId signalChannelRtEntityId)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        using var session = await tenantRepository.GetSessionAsync();
        try
        {
            session.StartTransaction();

            var entityUpdateInfoList = new List<EntityUpdateInfo<RtSignalChannel>>
            {
                EntityUpdateInfo<RtSignalChannel>.CreateDelete(signalChannelRtEntityId)
            };

            // Erase, not the default Archive: the stored SignalChannel entities are the
            // instance-wide number-claim registry (AB#5143) — an archived tombstone must not keep
            // the number blocked for the next claimant.
            OperationResult operationResult = new();
            await tenantRepository.ApplyChangesAsync(session, entityUpdateInfoList, DeleteOptions.Erase,
                operationResult);
            if (operationResult.HasErrors || operationResult.HasFatalErrors)
            {
                throw CommunicationRepositoryException.CommonOperationFailed(operationResult);
            }

            await session.CommitTransactionAsync();
        }
        catch (CommunicationRepositoryException)
        {
            throw;
        }
        catch (Exception e)
        {
            throw CommunicationRepositoryException.CommonFailedDeletingSignalChannel(tenantId,
                signalChannelRtEntityId.RtId, e);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyCollection<RtDeployableWorkload>> GetWorkloadsByChartNameAsync(string tenantId,
        string chartName)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        using var session = await tenantRepository.GetSessionAsync();
        try
        {
            // RtDeployableWorkload is abstract; the runtime engine returns
            // the concrete RtAdapter / RtApplication polymorphically. CI/CD
            // callers don't care about the subtype — they just need ChartName +
            // ChartVersion + RtId, all of which are on the base type.
            var queryOptions = RtEntityQueryOptions.Create()
                .FieldFilter(nameof(RtDeployableWorkload.ChartName), FieldFilterOperator.Equals, chartName);
            var resultSet = await tenantRepository
                .GetRtEntitiesByTypeAsync<RtDeployableWorkload>(session, queryOptions);
            return resultSet.Items.ToList();
        }
        catch (Exception e)
        {
            throw CommunicationRepositoryException.CommonFailedGettingAdapters(tenantId, e);
        }
    }

    /// <inheritdoc />
    public async Task<string?> UpdateWorkloadChartVersionAsync(string tenantId, OctoObjectId workloadRtId,
        string newChartVersion)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        using var session = await tenantRepository.GetSessionAsync();
        try
        {
            // Load → mutate → replace. ReplaceOne preserves the concrete CK type
            // (RtAdapter vs RtApplication) since we operate on the polymorphic
            // base. No transactional commit needed — single-document write.
            var workload = await tenantRepository.GetRtEntityByRtIdAsync<RtDeployableWorkload>(session, workloadRtId);
            if (workload == null)
            {
                throw CommunicationRepositoryException.CommonFailedGettingAdapters(tenantId, workloadRtId,
                    new InvalidOperationException($"Workload '{workloadRtId}' not found"));
            }

            var previousVersion = workload.ChartVersion;
            workload.ChartVersion = newChartVersion;
            await tenantRepository.ReplaceOneRtEntityByIdAsync(session, workloadRtId, workload);
            return previousVersion;
        }
        catch (CommunicationRepositoryException)
        {
            throw;
        }
        catch (Exception e)
        {
            throw CommunicationRepositoryException.CommonFailedGettingAdapters(tenantId, workloadRtId, e);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyCollection<RtAdapter>> GetAdaptersAsync(string tenantId)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        using var session = await tenantRepository.GetSessionAsync();
        try
        {
            var resultSet =
                await tenantRepository.GetRtEntitiesByTypeAsync<RtAdapter>(session, RtEntityQueryOptions.Create());

            return resultSet.Items.ToList();
        }
        catch (Exception e)
        {
            throw CommunicationRepositoryException.CommonFailedGettingAdapters(tenantId, e);
        }
    }

    /// <inheritdoc />
    public async Task<RtAdapter> GetAdapterAsync(string tenantId, RtEntityId adapterRtEntityId)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        using var session = await tenantRepository.GetSessionAsync();
        try
        {
            var adapter = await tenantRepository.GetRtEntityByRtIdAsync<RtAdapter>(session, adapterRtEntityId.RtId);

            if (adapter == null)
            {
                throw CommunicationRepositoryException.AdapterNotFound(tenantId, adapterRtEntityId);
            }

            if (adapter.CkTypeId == null)
            {
                throw CommunicationRepositoryException.AdapterTypeMissing(tenantId, adapterRtEntityId);
            }

            if (adapter.CkTypeId != adapterRtEntityId.CkTypeId)
            {
                throw CommunicationRepositoryException.AdapterTypeMismatch(tenantId, adapterRtEntityId,
                    adapter.CkTypeId);
            }

            return adapter;
        }
        catch (CommunicationRepositoryException)
        {
            throw;
        }
        catch (Exception e)
        {
            throw CommunicationRepositoryException.CommonFailedGettingAdapter(tenantId, adapterRtEntityId, e);
        }
    }

    public async Task<RtAdapter?> GetAdapterByPipelineAsync(string tenantId, RtEntityId pipelineRtEntityId)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        using var session = await tenantRepository.GetSessionAsync();
        try
        {
            var multipleOriginResultSet =
                await tenantRepository.GetRtAssociationTargetsAsync<RtPipeline, RtAdapter>(
                    session,
                    [pipelineRtEntityId.RtId], SystemCommunicationCkIds.RtCkExecutesRoleId,
                    GraphDirections.Outbound, null, RtEntityQueryOptions.Create());

            if (multipleOriginResultSet.Any())
            {
                return multipleOriginResultSet.First().Value.Items.FirstOrDefault();
            }

            throw CommunicationRepositoryException.PipelineNotFound(tenantId, pipelineRtEntityId.RtId);
        }
        catch (CommunicationRepositoryException)
        {
            throw;
        }
        catch (Exception e)
        {
            throw CommunicationRepositoryException.CommonFailedGettingAdapterByPipeline(tenantId, pipelineRtEntityId, e);
        }
    }

    /// <inheritdoc />
    public async Task<PipelineMoveResult> MovePipelineToAdapterAsync(string tenantId, OctoObjectId pipelineRtId,
        OctoObjectId targetAdapterRtId)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        using var session = await tenantRepository.GetSessionAsync();
        // The association swap is a write — start a transaction before any
        // call to ApplyChangesAsync so the CommitTransactionAsync below has
        // something to commit. Without this, CommitTransactionAsync throws
        // "There is no transaction started" and we surface the generic
        // CommonFailedMovePipeline error to the UI.
        session.StartTransaction();
        try
        {
            var pipeline = await tenantRepository.GetRtEntityByRtIdAsync<RtPipeline>(session, pipelineRtId);
            if (pipeline == null)
            {
                throw CommunicationRepositoryException.PipelineNotFound(tenantId, pipelineRtId);
            }

            // The source adapter is whatever currently sits on the outbound
            // Pipeline.Executes edge. Pipelines must have exactly one such
            // edge — refuse to "move" an orphan because that would silently
            // mutate it from "unassigned" to "assigned to target".
            var sourceAdapterResultSet =
                await tenantRepository.GetRtAssociationTargetsAsync<RtPipeline, RtAdapter>(
                    session,
                    [pipelineRtId], SystemCommunicationCkIds.RtCkExecutesRoleId,
                    GraphDirections.Outbound, null, RtEntityQueryOptions.Create());
            var sourceAdapter = sourceAdapterResultSet.Any()
                ? sourceAdapterResultSet.First().Value.Items.FirstOrDefault()
                : null;
            if (sourceAdapter == null)
            {
                throw CommunicationRepositoryException.PipelineHasNoAdapter(tenantId, pipelineRtId);
            }

            var targetAdapter =
                await tenantRepository.GetRtEntityByRtIdAsync<RtAdapter>(session, targetAdapterRtId);
            if (targetAdapter == null)
            {
                throw CommunicationRepositoryException.AdapterNotFound(tenantId,
                    new RtEntityId(SystemCommunicationCkIds.RtCkAdapterTypeId, targetAdapterRtId));
            }

            if (sourceAdapter.CkTypeId == null || targetAdapter.CkTypeId == null ||
                !sourceAdapter.CkTypeId.Equals(targetAdapter.CkTypeId))
            {
                throw CommunicationRepositoryException.AdapterTypeMismatchForMove(tenantId, pipelineRtId,
                    sourceAdapter.CkTypeId!, targetAdapter.CkTypeId!);
            }

            var sourceAdapterRtEntityId = new RtEntityId(sourceAdapter.CkTypeId!, sourceAdapter.RtId);
            var targetAdapterRtEntityId = new RtEntityId(targetAdapter.CkTypeId!, targetAdapter.RtId);

            // No-op when the pipeline already points at the target. Return
            // success so the caller sees a deterministic outcome without
            // having to special-case "already there".
            if (sourceAdapter.RtId.Equals(targetAdapter.RtId))
            {
                return new PipelineMoveResult(pipelineRtId, sourceAdapterRtEntityId, targetAdapterRtEntityId);
            }

            var pipelineRtEntityId =
                new RtEntityId(SystemCommunicationCkIds.RtCkPipelineTypeId, pipelineRtId);

            var associations = new List<AssociationUpdateInfo>
            {
                AssociationUpdateInfo.CreateDelete(pipelineRtEntityId, sourceAdapterRtEntityId,
                    SystemCommunicationCkIds.RtCkExecutesRoleId),
                AssociationUpdateInfo.CreateInsert(pipelineRtEntityId, targetAdapterRtEntityId,
                    SystemCommunicationCkIds.RtCkExecutesRoleId)
            };

            OperationResult operationResult = new();
            await tenantRepository.ApplyChangesAsync(session, new List<EntityUpdateInfo<RtPipeline>>(),
                associations, operationResult);
            if (operationResult.HasErrors || operationResult.HasFatalErrors)
            {
                throw CommunicationRepositoryException.CommonOperationFailed(operationResult);
            }

            await session.CommitTransactionAsync();

            return new PipelineMoveResult(pipelineRtId, sourceAdapterRtEntityId, targetAdapterRtEntityId);
        }
        catch (CommunicationRepositoryException)
        {
            throw;
        }
        catch (Exception e)
        {
            throw CommunicationRepositoryException.CommonFailedMovePipeline(tenantId, pipelineRtId,
                targetAdapterRtId, e);
        }
    }

    public async Task<IReadOnlyCollection<RtDeploymentSite>> GetDeploymentSitesAsync(string tenantId)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        using var session = await tenantRepository.GetSessionAsync();
        try
        {
            var dataQueryOperation = RtEntityQueryOptions.Create();
            var poolResultSet = await tenantRepository.GetRtEntitiesByTypeAsync<RtDeploymentSite>(session, dataQueryOperation);

            return poolResultSet.Items.ToList();
        }
        catch (Exception e)
        {
            throw CommunicationRepositoryException.CommonFailedGettingPools(tenantId, e);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyCollection<RtDeploymentSite>> GetPoolByNameAsync(string tenantId, string deploymentSiteName)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        using var session = await tenantRepository.GetSessionAsync();
        try
        {
            var dataQueryOperation = RtEntityQueryOptions.Create()
                .FieldFilter(nameof(RtDeploymentSite.Name), FieldFilterOperator.Equals, deploymentSiteName);

            var poolResultSet = await tenantRepository.GetRtEntitiesByTypeAsync<RtDeploymentSite>(session, dataQueryOperation);

            return poolResultSet.Items.ToList();
        }
        catch (Exception e)
        {
            throw CommunicationRepositoryException.CommonFailedGettingPoolByName(tenantId, deploymentSiteName, e);
        }
    }

    /// <inheritdoc />
    public async Task CreatePoolAsync(string tenantId, string deploymentSiteName)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        using var session = await tenantRepository.GetSessionAsync();
        try
        {
            session.StartTransaction();

            var rtDeploymentSite = new RtDeploymentSite
            {
                CommunicationState = RtCommunicationStateEnum.Offline,
                DeploymentState = RtDeploymentStateEnum.Undeployed,
                ConfigurationState = RtConfigurationStateEnum.Unconfigured,
                Name = deploymentSiteName
            };

            var entityUpdateInfoList = new List<EntityUpdateInfo<RtDeploymentSite>>
            {
                EntityUpdateInfo<RtDeploymentSite>.CreateInsert(rtDeploymentSite)
            };

            OperationResult operationResult = new();
            await tenantRepository.ApplyChangesAsync(session, entityUpdateInfoList, operationResult);
            if (operationResult.HasErrors || operationResult.HasFatalErrors)
            {
                throw CommunicationRepositoryException.CommonOperationFailed(operationResult);
            }

            await session.CommitTransactionAsync();
        }
        catch (CommunicationRepositoryException)
        {
            throw;
        }
        catch (Exception e)
        {
            throw CommunicationRepositoryException.CommonFailedCreatePool(tenantId, deploymentSiteName, e);
        }
    }

    public async Task SetDeploymentSiteDeploymentStateAsync(string tenantId, OctoObjectId adapterPoolRtId,
        RtDeploymentStateEnum deploymentState)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        using var session = await tenantRepository.GetSessionAsync();
        try
        {
            session.StartTransaction();

            var rtDeploymentSite = new RtDeploymentSite
            {
                RtId = adapterPoolRtId,
                DeploymentState = deploymentState
            };

            var entityUpdateInfoList = new List<EntityUpdateInfo<RtDeploymentSite>>
            {
                EntityUpdateInfo<RtDeploymentSite>.CreateUpdate(rtDeploymentSite.ToRtEntityId(), rtDeploymentSite)
            };

            OperationResult operationResult = new();
            await tenantRepository.ApplyChangesAsync(session, entityUpdateInfoList, operationResult);
            if (operationResult.HasErrors || operationResult.HasFatalErrors)
            {
                throw CommunicationRepositoryException.CommonOperationFailed(operationResult);
            }

            await session.CommitTransactionAsync();
        }
        catch (CommunicationRepositoryException)
        {
            throw;
        }
        catch (Exception e)
        {
            throw CommunicationRepositoryException.CommonFailedSetPoolDeploymentState(tenantId, adapterPoolRtId,
                deploymentState, e);
        }
    }

    public async Task SetDeploymentSiteCommunicationStateAsync(string tenantId, OctoObjectId adapterPoolRtId,
        RtCommunicationStateEnum communicationState)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        using var session = await tenantRepository.GetSessionAsync();
        try
        {
            session.StartTransaction();

            // Capture the timestamp once; the same value goes into the entity (so it lands
            // in the DB) AND into the guard (so a stale write with an older timestamp from
            // a parallel writer — e.g. a controller pod mid-shutdown — is rejected at the
            // MongoDB filter level).
            var newTimestamp = DateTime.UtcNow;
            var rtDeploymentSite = new RtDeploymentSite
            {
                RtId = adapterPoolRtId,
                CommunicationState = communicationState,
                CommunicationStateTimestamp = newTimestamp
            };

            var guard = new AttributeNewerThanGuard("attributes.communicationStateTimestamp", newTimestamp);
            var entityUpdateInfoList = new List<EntityUpdateInfo<RtDeploymentSite>>
            {
                EntityUpdateInfo<RtDeploymentSite>.CreateConditionalUpdate(rtDeploymentSite.ToRtEntityId(), rtDeploymentSite, guard)
            };

            OperationResult operationResult = new();
            await tenantRepository.ApplyChangesAsync(session, entityUpdateInfoList, operationResult);
            if (operationResult.HasErrors || operationResult.HasFatalErrors)
            {
                throw CommunicationRepositoryException.CommonOperationFailed(operationResult);
            }

            await session.CommitTransactionAsync();
        }
        catch (CommunicationRepositoryException)
        {
            throw;
        }
        catch (Exception e)
        {
            throw CommunicationRepositoryException.CommonFailedSetPoolCommunicationState(tenantId, adapterPoolRtId,
                communicationState, e);
        }
    }

    public async Task SetAdapterDeploymentStateAsync(string tenantId, RtEntityId adapterRtEntityId,
        RtDeploymentStateEnum deploymentState, string? stateMessage = null)
    {
        await SetAdapterDeploymentStateAsync(tenantId, [adapterRtEntityId], deploymentState, stateMessage);
    }

    public async Task SetAdapterDeploymentStateAsync(string tenantId, ICollection<RtEntityId> adapterRtEntityIds,
        RtDeploymentStateEnum deploymentState, string? stateMessage = null)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        using var session = await tenantRepository.GetSessionAsync();
        try
        {
            session.StartTransaction();

            var rtAdapter = new RtAdapter
            {
                DeploymentState = deploymentState,
                StatusMessage = stateMessage
            };
            ApplyDeploymentErrorTracking(rtAdapter, deploymentState, stateMessage);

            // Invariant: ConfigurationState == Configured implies DeploymentState == Deployed.
            // When the workload has no running pod (Undeployed / Disabled) or its deploy failed
            // (Error), the previously-pushed config is no longer in effect — force-reset
            // ConfigurationState so the UI badge follows reality. We keep ConfigurationState
            // alone during Deployed (the pod is back, config still applies until proven
            // otherwise) and Pending (a redeploy is in flight — the config window has not
            // closed yet). LastConfigurationError is preserved by Unconfigured so any prior
            // configuration failure stays visible.
            if (deploymentState == RtDeploymentStateEnum.Undeployed ||
                deploymentState == RtDeploymentStateEnum.Disabled ||
                deploymentState == RtDeploymentStateEnum.Error)
            {
                rtAdapter.ConfigurationState = RtConfigurationStateEnum.Unconfigured;
            }

            var entityUpdateInfoList = new List<EntityUpdateInfo<RtAdapter>>();
            foreach (var adapterRtEntityId in adapterRtEntityIds)
            {
                entityUpdateInfoList.Add(EntityUpdateInfo<RtAdapter>.CreateUpdate(adapterRtEntityId, rtAdapter));
            }

            OperationResult operationResult = new();
            await tenantRepository.ApplyChangesAsync(session, entityUpdateInfoList, operationResult);
            if (operationResult.HasErrors || operationResult.HasFatalErrors)
            {
                throw CommunicationRepositoryException.CommonOperationFailed(operationResult);
            }

            await session.CommitTransactionAsync();
        }
        catch (CommunicationRepositoryException)
        {
            throw;
        }
        catch (Exception e)
        {
            throw CommunicationRepositoryException.CommonFailedSetAdapterDeploymentState(tenantId, adapterRtEntityIds,
                deploymentState, e);
        }
    }

    public async Task SetApplicationDeploymentStateAsync(string tenantId, RtEntityId applicationRtEntityId,
        RtDeploymentStateEnum deploymentState, string? stateMessage = null)
    {
        await SetApplicationDeploymentStateAsync(tenantId, [applicationRtEntityId], deploymentState, stateMessage);
    }

    public async Task SetApplicationDeploymentStateAsync(string tenantId, ICollection<RtEntityId> applicationRtEntityIds,
        RtDeploymentStateEnum deploymentState, string? stateMessage = null)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        using var session = await tenantRepository.GetSessionAsync();
        try
        {
            session.StartTransaction();

            var rtApplication = new RtApplication
            {
                DeploymentState = deploymentState,
                StatusMessage = stateMessage
            };
            ApplyDeploymentErrorTracking(rtApplication, deploymentState, stateMessage);

            var entityUpdateInfoList = new List<EntityUpdateInfo<RtApplication>>();
            foreach (var applicationRtEntityId in applicationRtEntityIds)
            {
                entityUpdateInfoList.Add(EntityUpdateInfo<RtApplication>.CreateUpdate(applicationRtEntityId, rtApplication));
            }

            OperationResult operationResult = new();
            await tenantRepository.ApplyChangesAsync(session, entityUpdateInfoList, operationResult);
            if (operationResult.HasErrors || operationResult.HasFatalErrors)
            {
                throw CommunicationRepositoryException.CommonOperationFailed(operationResult);
            }

            await session.CommitTransactionAsync();
        }
        catch (CommunicationRepositoryException)
        {
            throw;
        }
        catch (Exception e)
        {
            throw CommunicationRepositoryException.CommonFailedSetApplicationDeploymentState(tenantId,
                applicationRtEntityIds, deploymentState, e);
        }
    }

    /// <inheritdoc />
    public async Task SetAdapterPoolDeploymentStateAsync(string tenantId, RtEntityId adapterPoolRtEntityId,
        RtDeploymentStateEnum deploymentState, string? stateMessage = null)
    {
        await SetAdapterPoolDeploymentStateAsync(tenantId, [adapterPoolRtEntityId], deploymentState, stateMessage);
    }

    /// <inheritdoc />
    public async Task SetAdapterPoolDeploymentStateAsync(string tenantId,
        ICollection<RtEntityId> adapterPoolRtEntityIds, RtDeploymentStateEnum deploymentState,
        string? stateMessage = null)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        using var session = await tenantRepository.GetSessionAsync();
        try
        {
            session.StartTransaction();

            var rtAdapterPool = new RtAdapterPool
            {
                DeploymentState = deploymentState,
                StatusMessage = stateMessage
            };
            ApplyDeploymentErrorTracking(rtAdapterPool, deploymentState, stateMessage);

            // No ConfigurationState reset here, unlike the adapter path: a pool has no
            // configuration push of its own. Its members are configured per lease, by the
            // borrower's definitions, and there is no per-pool config state to invalidate.

            var entityUpdateInfoList = new List<EntityUpdateInfo<RtAdapterPool>>();
            foreach (var adapterPoolRtEntityId in adapterPoolRtEntityIds)
            {
                entityUpdateInfoList.Add(
                    EntityUpdateInfo<RtAdapterPool>.CreateUpdate(adapterPoolRtEntityId, rtAdapterPool));
            }

            OperationResult operationResult = new();
            await tenantRepository.ApplyChangesAsync(session, entityUpdateInfoList, operationResult);
            if (operationResult.HasErrors || operationResult.HasFatalErrors)
            {
                throw CommunicationRepositoryException.CommonOperationFailed(operationResult);
            }

            await session.CommitTransactionAsync();
        }
        catch (CommunicationRepositoryException)
        {
            throw;
        }
        catch (Exception e)
        {
            throw CommunicationRepositoryException.CommonFailedSetAdapterPoolDeploymentState(tenantId,
                adapterPoolRtEntityIds, deploymentState, e);
        }
    }

    public async Task SetAdapterCommunicationStateAsync(string tenantId, RtEntityId adapterRtEntityId,
        RtCommunicationStateEnum communicationState)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        using var session = await tenantRepository.GetSessionAsync();
        try
        {
            session.StartTransaction();

            // Capture the timestamp once; the same value goes into the entity (so it lands
            // in the DB) AND into the guard (so a stale write with an older timestamp from
            // a parallel writer — e.g. a controller pod mid-shutdown whose OnDisconnectedAsync
            // commits late, after the replacement pod has already written Online — is
            // rejected at the MongoDB filter level instead of clobbering the newer state).
            var newTimestamp = DateTime.UtcNow;
            var rtAdapter = new RtAdapter
            {
                CommunicationState = communicationState,
                CommunicationStateTimestamp = newTimestamp
            };

            // Invariant: ConfigurationState == Configured implies CommunicationState == Online.
            // A pod that is Offline / Unregistered cannot still be running the pushed config —
            // surfacing "Configured" in the UI while the adapter is unreachable misleads the
            // user. Force-reset to Unconfigured so the badge follows reality. The persistent
            // LastConfigurationError is NOT cleared (Unconfigured doesn't trigger the clear
            // branch in ApplyConfigurationErrorTracking), so any prior error context stays
            // visible across the offline window.
            if (communicationState != RtCommunicationStateEnum.Online)
            {
                rtAdapter.ConfigurationState = RtConfigurationStateEnum.Unconfigured;
            }

            var guard = new AttributeNewerThanGuard("attributes.communicationStateTimestamp", newTimestamp);
            var entityUpdateInfoList = new List<EntityUpdateInfo<RtAdapter>>
            {
                EntityUpdateInfo<RtAdapter>.CreateConditionalUpdate(adapterRtEntityId, rtAdapter, guard)
            };

            OperationResult operationResult = new();
            await tenantRepository.ApplyChangesAsync(session, entityUpdateInfoList, operationResult);
            if (operationResult.HasErrors || operationResult.HasFatalErrors)
            {
                throw CommunicationRepositoryException.CommonOperationFailed(operationResult);
            }

            await session.CommitTransactionAsync();
        }
        catch (CommunicationRepositoryException)
        {
            throw;
        }
        catch (Exception e)
        {
            throw CommunicationRepositoryException.CommonFailedSetAdapterCommunicationState(tenantId, adapterRtEntityId,
                communicationState, e);
        }
    }

    /// <inheritdoc />
    public async Task<RtDeploymentSite> GetPoolOfAdapterAsync(string tenantId, RtEntityId adapterRtEntityId)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        using var session = await tenantRepository.GetSessionAsync();
        try
        {
            var poolResultSet = await tenantRepository.GetRtAssociationTargetsAsync<RtAdapter, RtDeploymentSite>(session,
                [adapterRtEntityId.RtId],
                SystemCommunicationCkIds.RtCkHostsRoleId,
                GraphDirections.Inbound, null, RtEntityQueryOptions.Create());

            if (poolResultSet.Any())
            {
                var pool = poolResultSet.First().Value.Items.FirstOrDefault();
                if (pool != null)
                {
                    return pool;
                }

                throw CommunicationRepositoryException.AdapterNotAssociatedToDeploymentSite(tenantId, adapterRtEntityId);
            }

            throw CommunicationRepositoryException.AdapterNotFound(tenantId, adapterRtEntityId);
        }
        catch (CommunicationRepositoryException)
        {
            throw;
        }
        catch (Exception e)
        {
            throw CommunicationRepositoryException.CommonGettingPoolOfAdapter(tenantId, adapterRtEntityId, e);
        }
    }

    public async Task<bool> IsTenantExistingAsync(string tenantId)
    {
        var systemSession = await _systemContext.GetAdminSessionAsync();

        try
        {
            var isTenantExisting = await _systemContext.IsChildTenantExistingAsync(systemSession, tenantId);

            return isTenantExisting;
        }
        catch (Exception e)
        {
            throw CommunicationRepositoryException.CommonFailedIsTenantExisting(tenantId, e);
        }
    }

    public async Task<IReadOnlyCollection<RtPipeline>> GetPipelinesAsync(string tenantId, RtEntityId adapterRtEntityId)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        using var session = await tenantRepository.GetSessionAsync();
        try
        {
            var multipleOriginResultSet = await tenantRepository.GetRtAssociationTargetsAsync<RtAdapter, RtPipeline>(
                session,
                [adapterRtEntityId.RtId],
                SystemCommunicationCkIds.RtCkExecutesRoleId,
                GraphDirections.Inbound, null, RtEntityQueryOptions.Create());

            if (multipleOriginResultSet.Any())
            {
                return multipleOriginResultSet.First().Value.Items.ToList();
            }

            throw CommunicationRepositoryException.AdapterNotFound(tenantId, adapterRtEntityId);
        }
        catch (CommunicationRepositoryException)
        {
            throw;
        }
        catch (Exception e)
        {
            throw CommunicationRepositoryException.CommonFailedGettingAdapter(tenantId, adapterRtEntityId, e);
        }
    }

    public async Task<IReadOnlyCollection<RtPipeline>> GetPipelinesAsync(string tenantId, OctoObjectId dataFlowRtId)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        using var session = await tenantRepository.GetSessionAsync();
        try
        {
            var originResultSet = await tenantRepository.GetRtAssociationTargetsAsync<RtDataFlow, RtPipeline>(session,
                [dataFlowRtId],
                SystemCkIds.RtCkParentChildRoleId,
                GraphDirections.Inbound, null, RtEntityQueryOptions.Create());

            if (originResultSet.Any())
            {
                var pool = originResultSet.First().Value.Items;
                return pool.ToList();
            }

            throw CommunicationRepositoryException.DataFlowNotFound(tenantId, dataFlowRtId);
        }
        catch (CommunicationRepositoryException)
        {
            throw;
        }
        catch (Exception e)
        {
            throw CommunicationRepositoryException.CommonFailedGettingByDataFlow(tenantId, dataFlowRtId, e);
        }
    }

    public async Task<RtPipeline?> GetPipelineAsync(string tenantId, RtEntityId pipelineRtEntityId)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        using var session = await tenantRepository.GetSessionAsync();
        try
        {
            var rtPipeline =
                await tenantRepository.GetRtEntityByRtIdAsync<RtPipeline>(session, pipelineRtEntityId.RtId);
            return rtPipeline;
        }
        catch (CommunicationRepositoryException)
        {
            throw;
        }
        catch (Exception e)
        {
            throw CommunicationRepositoryException.CommonFailedGettingPipeline(tenantId, pipelineRtEntityId, e);
        }
    }

    public async Task<RtDataFlow?> GetDataFlowByPipelineAsync(string tenantId, OctoObjectId pipelineRtId)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        using var session = await tenantRepository.GetSessionAsync();
        try
        {
            var multipleOriginResultSet =
                await tenantRepository.GetRtAssociationTargetsAsync<RtPipeline, RtDataFlow>(
                    session,
                    [pipelineRtId],
                    SystemCkIds.RtCkParentChildRoleId,
                    GraphDirections.Outbound, null, RtEntityQueryOptions.Create());

            if (multipleOriginResultSet.Any())
            {
                return multipleOriginResultSet.First().Value.Items.FirstOrDefault();
            }

            throw CommunicationRepositoryException.PipelineNotFound(tenantId, pipelineRtId);
        }
        catch (CommunicationRepositoryException)
        {
            throw;
        }
        catch (Exception e)
        {
            throw CommunicationRepositoryException.CommonFailedGettingPipeline(tenantId, pipelineRtId, e);
        }
    }
    
    public async Task<IEnumerable<RtConfiguration>> GetConfigurationsByPipelineAsync(string tenantId, OctoObjectId pipelineRtId)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        using var session = await tenantRepository.GetSessionAsync();
        try
        {
            var multipleOriginResultSet =
                await tenantRepository.GetRtAssociationTargetsAsync<RtPipeline, RtConfiguration>(
                    session,
                    [pipelineRtId],
                    SystemCommunicationCkIds.RtCkUsesRoleId,
                    GraphDirections.Outbound, null, RtEntityQueryOptions.Create());

            if (multipleOriginResultSet.Any())
            {
                return multipleOriginResultSet.First().Value.Items;
            }
            throw CommunicationRepositoryException.PipelineNotFound(tenantId, pipelineRtId);
        }
        catch (CommunicationRepositoryException)
        {
            throw;
        }
        catch (Exception e)
        {
            throw CommunicationRepositoryException.CommonFailedGettingConfiguration(tenantId, pipelineRtId, e);
        }
    }

    public async Task<IReadOnlyCollection<RtPipelineTrigger>> GetTriggersAsync(string tenantId)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        using var session = await tenantRepository.GetSessionAsync();
        try
        {
            var queryOptions = RtEntityQueryOptions.Create();
            var r = await tenantRepository.GetRtEntitiesByTypeAsync<RtPipelineTrigger>(session, queryOptions);

            return r.Items.ToList();
        }
        catch (Exception e)
        {
            throw CommunicationRepositoryException.CommonFailedGettingTriggers(tenantId, e);
        }
    }

    public async Task<IDictionary<RtPipelineTrigger, IList<RtPipeline>>> GetTriggersAndPipelinesAsync(
        string tenantId)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        using var session = await tenantRepository.GetSessionAsync();
        try
        {
            var queryOptions = RtEntityQueryOptions.Create()
                .FieldEquals(nameof(RtPipelineTrigger.Enabled), true);

            var r = await tenantRepository.GetRtEntitiesByTypeAsync<RtPipelineTrigger>(session, queryOptions);

            queryOptions = RtEntityQueryOptions.Create();
            // PipelineTrigger is the origin of the Triggers association (PipelineTrigger → Pipeline)
            // We need to find Pipelines that are targets of the Trigger's association
            var a = await tenantRepository.GetRtAssociationTargetsAsync<RtPipelineTrigger, RtPipeline>(session,
                r.Items.Select(x => x.RtId).ToList(),
                SystemCommunicationCkIds.RtCkTriggersRoleId, GraphDirections.Outbound, null, queryOptions);

            Dictionary<RtPipelineTrigger, IList<RtPipeline>> list = new();
            foreach (var pipelineTrigger in r.Items)
            {
                if (a.TryGetValue(pipelineTrigger.ToRtEntityId(), out var resultSet))
                {
                    list.Add(pipelineTrigger, resultSet.Items.ToList());
                }
            }

            return list;
        }
        catch (Exception e)
        {
            throw CommunicationRepositoryException.CommonFailedGettingTriggers(tenantId, e);
        }
    }

    public async Task SetPipelineTriggerDeploymentStateAsync(string tenantId, OctoObjectId triggerRtId,
        RtDeploymentStateEnum deploymentState)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        using var session = await tenantRepository.GetSessionAsync();
        try
        {
            session.StartTransaction();

            var pipelineTrigger = new RtPipelineTrigger
            {
                DeploymentState = deploymentState
            };

            var entityUpdateInfoList = new List<EntityUpdateInfo<RtPipelineTrigger>>
            {
                EntityUpdateInfo<RtPipelineTrigger>.CreateUpdate(
                    new RtEntityId(SystemCommunicationCkIds.RtCkPipelineTriggerTypeId,
                        triggerRtId),
                    pipelineTrigger)
            };

            OperationResult operationResult = new();
            await tenantRepository.ApplyChangesAsync(session, entityUpdateInfoList, operationResult);
            if (operationResult.HasErrors || operationResult.HasFatalErrors)
            {
                throw CommunicationRepositoryException.CommonOperationFailed(operationResult);
            }

            await session.CommitTransactionAsync();
        }
        catch (CommunicationRepositoryException)
        {
            throw;
        }
        catch (Exception e)
        {
            throw CommunicationRepositoryException.CommonFailedSetTriggerDeploymentState(tenantId, triggerRtId,
                deploymentState, e);
        }
    }

    public async Task SetPipelineDeploymentStateAsync(string tenantId, RtEntityId pipelineRtEntityId,
        RtDeploymentStateEnum deploymentState, string? stateMessage)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        using var session = await tenantRepository.GetSessionAsync();
        try
        {
            session.StartTransaction();

            var pipeline = new RtPipeline
            {
                DeploymentState = deploymentState,
                StatusMessage = stateMessage
            };
            ApplyDeploymentErrorTracking(pipeline, deploymentState, stateMessage);

            var entityUpdateInfoList = new List<EntityUpdateInfo<RtPipeline>>
            {
                EntityUpdateInfo<RtPipeline>.CreateUpdate(
                    pipelineRtEntityId,
                    pipeline)
            };

            OperationResult operationResult = new();
            await tenantRepository.ApplyChangesAsync(session, entityUpdateInfoList, operationResult);
            if (operationResult.HasErrors || operationResult.HasFatalErrors)
            {
                throw CommunicationRepositoryException.CommonOperationFailed(operationResult);
            }

            await session.CommitTransactionAsync();
        }
        catch (CommunicationRepositoryException)
        {
            throw;
        }
        catch (Exception e)
        {
            throw CommunicationRepositoryException.CommonFailedSetPipelineDeploymentState(tenantId, pipelineRtEntityId,
                deploymentState, e);
        }
    }

    /// <inheritdoc />
    public async Task SetPipelineExecutionClassAsync(string tenantId, RtEntityId pipelineRtEntityId,
        int executionClass)
    {
        // AB#4924 — single-field writer for the redeploy path, where the YAML is unchanged but the
        // class may not be: it depends on the ADAPTER's descriptors as well as the definition, so
        // moving a pipeline to an adapter on a different SDK can legitimately change it.
        //
        // Deliberately NOT a SetPipelineDefinitionAsync call with the existing definition: that
        // would rewrite PipelineDefinition on every redeploy, which breaks the invariant
        // "deploy must not persist a definition when none was provided" (AB#4364's neighbour, and
        // a test pins it).
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        using var session = await tenantRepository.GetSessionAsync();
        try
        {
            session.StartTransaction();

            var pipeline = new RtPipeline
            {
                ExecutionClass = (RtPipelineExecutionClassEnum)executionClass
            };

            var entityUpdateInfoList = new List<EntityUpdateInfo<RtPipeline>>
            {
                EntityUpdateInfo<RtPipeline>.CreateUpdate(pipelineRtEntityId, pipeline)
            };

            OperationResult operationResult = new();
            await tenantRepository.ApplyChangesAsync(session, entityUpdateInfoList, operationResult);
            if (operationResult.HasErrors || operationResult.HasFatalErrors)
            {
                throw CommunicationRepositoryException.CommonOperationFailed(operationResult);
            }

            await session.CommitTransactionAsync();
        }
        catch (Exception e)
        {
            await session.AbortTransactionAsync();
            _logger.LogWarning(e, "[{TenantId}] Failed to set execution class on pipeline '{PipelineRtEntityId}'",
                tenantId, pipelineRtEntityId);
        }
    }

    public async Task SetPipelineDefinitionAsync(string tenantId, RtEntityId pipelineRtEntityId,
        string pipelineDefinition, int? executionClass = null)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        using var session = await tenantRepository.GetSessionAsync();
        try
        {
            session.StartTransaction();

            // AB#4924 — the execution class is written in the SAME update as the definition it was
            // derived from, deliberately, so the two can never disagree. A separate write would
            // leave a window in which the persisted class describes the previous YAML, and that
            // window is exactly when somebody reads the queue to ask why their job is waiting.
            var pipeline = new RtPipeline
            {
                PipelineDefinition = pipelineDefinition
            };

            if (executionClass.HasValue)
            {
                pipeline.ExecutionClass = (RtPipelineExecutionClassEnum)executionClass.Value;
            }

            var entityUpdateInfoList = new List<EntityUpdateInfo<RtPipeline>>
            {
                EntityUpdateInfo<RtPipeline>.CreateUpdate(
                    pipelineRtEntityId,
                    pipeline)
            };

            OperationResult operationResult = new();
            await tenantRepository.ApplyChangesAsync(session, entityUpdateInfoList, operationResult);
            if (operationResult.HasErrors || operationResult.HasFatalErrors)
            {
                throw CommunicationRepositoryException.CommonOperationFailed(operationResult);
            }

            await session.CommitTransactionAsync();
        }
        catch (CommunicationRepositoryException)
        {
            throw;
        }
        catch (Exception e)
        {
            throw CommunicationRepositoryException.CommonFailedSetPipelineDefinition(tenantId, pipelineRtEntityId, e);
        }

        // Sync SendsDataTo associations based on ToPipelineDataEvent nodes in the definition
        await SyncPipelineDataConnectionsAsync(tenantId, pipelineRtEntityId, pipelineDefinition);
    }

    /// <inheritdoc />
    public async Task SetPipelineDebuggingEnabledAsync(string tenantId, RtEntityId pipelineRtEntityId,
        bool isDebuggingEnabled)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        using var session = await tenantRepository.GetSessionAsync();
        try
        {
            session.StartTransaction();

            var pipeline = new RtPipeline
            {
                IsDebuggingEnabled = isDebuggingEnabled
            };

            var entityUpdateInfoList = new List<EntityUpdateInfo<RtPipeline>>
            {
                EntityUpdateInfo<RtPipeline>.CreateUpdate(
                    pipelineRtEntityId,
                    pipeline)
            };

            OperationResult operationResult = new();
            await tenantRepository.ApplyChangesAsync(session, entityUpdateInfoList, operationResult);
            if (operationResult.HasErrors || operationResult.HasFatalErrors)
            {
                throw CommunicationRepositoryException.CommonOperationFailed(operationResult);
            }

            await session.CommitTransactionAsync();
        }
        catch (CommunicationRepositoryException)
        {
            throw;
        }
        catch (Exception e)
        {
            throw CommunicationRepositoryException.CommonFailedSetPipelineDefinition(tenantId, pipelineRtEntityId, e);
        }
    }

    /// <inheritdoc />
    public async Task SyncPipelineDataConnectionsAsync(string tenantId, RtEntityId pipelineRtEntityId,
        string pipelineDefinition)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        using var session = await tenantRepository.GetSessionAsync();
        try
        {
            session.StartTransaction();

            // Parse target pipeline RtIds from ToPipelineDataEvent nodes in the YAML definition
            var targetPipelineRtIds = ParseTargetPipelineRtIds(pipelineDefinition);

            // Get existing SendsDataTo associations for this pipeline
            var existingTargets = await tenantRepository.GetRtAssociationTargetsAsync<RtPipeline, RtPipeline>(
                session,
                [pipelineRtEntityId.RtId],
                SystemCommunicationCkIds.RtCkSendsDataToRoleId,
                GraphDirections.Outbound,
                null,
                RtEntityQueryOptions.Create());

            var existingTargetIds = new HashSet<string>();
            if (existingTargets.TryGetValue(pipelineRtEntityId, out var existingResultSet))
            {
                foreach (var target in existingResultSet.Items)
                {
                    existingTargetIds.Add(target.RtId.ToString());
                }
            }

            var desiredTargetIds = new HashSet<string>(targetPipelineRtIds);
            var associations = new List<AssociationUpdateInfo>();

            // Add new associations
            foreach (var targetId in desiredTargetIds.Except(existingTargetIds))
            {
                if (OctoObjectId.TryParse(targetId, out var targetOid))
                {
                    var targetEntityId = new RtEntityId(SystemCommunicationCkIds.RtCkPipelineTypeId, targetOid);
                    associations.Add(AssociationUpdateInfo.CreateInsert(
                        pipelineRtEntityId, targetEntityId, SystemCommunicationCkIds.RtCkSendsDataToRoleId));
                }
            }

            // Remove stale associations
            foreach (var targetId in existingTargetIds.Except(desiredTargetIds))
            {
                if (OctoObjectId.TryParse(targetId, out var targetOid))
                {
                    var targetEntityId = new RtEntityId(SystemCommunicationCkIds.RtCkPipelineTypeId, targetOid);
                    associations.Add(AssociationUpdateInfo.CreateDelete(
                        pipelineRtEntityId, targetEntityId, SystemCommunicationCkIds.RtCkSendsDataToRoleId));
                }
            }

            if (associations.Count > 0)
            {
                OperationResult operationResult = new();
                await tenantRepository.ApplyChangesAsync(session, new List<EntityUpdateInfo<RtPipeline>>(),
                    associations, operationResult);
                if (operationResult.HasErrors || operationResult.HasFatalErrors)
                {
                    throw CommunicationRepositoryException.CommonOperationFailed(operationResult);
                }
            }

            await session.CommitTransactionAsync();
        }
        catch (CommunicationRepositoryException)
        {
            throw;
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Failed to sync pipeline data connections for {TenantId}/{PipelineRtId}",
                tenantId, pipelineRtEntityId.RtId);
        }
    }

    /// <summary>
    /// Parses the pipeline definition YAML/JSON and extracts targetPipelineRtId values
    /// from ToPipelineDataEvent transformation nodes.
    /// </summary>
    private static List<string> ParseTargetPipelineRtIds(string pipelineDefinition)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(pipelineDefinition))
            return result;

        try
        {
            var deserializer = new YamlDotNet.Serialization.DeserializerBuilder().Build();
            var yamlObject = deserializer.Deserialize<Dictionary<string, object>>(pipelineDefinition);
            if (yamlObject == null)
                return result;

            // Check transformations array
            if (yamlObject.TryGetValue("transformations", out var transformationsObj) &&
                transformationsObj is List<object> transformations)
            {
                foreach (var transformation in transformations)
                {
                    if (transformation is not Dictionary<object, object> transformDict)
                        continue;

                    // Check if this is a ToPipelineDataEvent node
                    if (transformDict.TryGetValue("type", out var typeObj) &&
                        typeObj is string typeStr &&
                        typeStr.StartsWith("ToPipelineDataEvent", StringComparison.OrdinalIgnoreCase))
                    {
                        if (transformDict.TryGetValue("targetPipelineRtId", out var targetIdObj) &&
                            targetIdObj is string targetId &&
                            !string.IsNullOrWhiteSpace(targetId))
                        {
                            result.Add(targetId);
                        }
                    }
                }
            }
        }
        catch (Exception)
        {
            // If YAML parsing fails, return empty list — don't prevent the save operation
        }

        return result;
    }

    public async Task SetAdapterConfigurationStateAsync(string tenantId, RtEntityId adapterRtEntityId,
        RtConfigurationStateEnum configurationState, string? stateMessage)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        using var session = await tenantRepository.GetSessionAsync();
        try
        {
            session.StartTransaction();

            var rtAdapter = new RtAdapter
            {
                ConfigurationState = configurationState,
                StatusMessage = stateMessage
            };
            ApplyConfigurationErrorTracking(rtAdapter, configurationState, stateMessage);

            var entityUpdateInfoList = new List<EntityUpdateInfo<RtAdapter>>
            {
                EntityUpdateInfo<RtAdapter>.CreateUpdate(adapterRtEntityId, rtAdapter)
            };

            OperationResult operationResult = new();
            await tenantRepository.ApplyChangesAsync(session, entityUpdateInfoList, operationResult);
            if (operationResult.HasErrors || operationResult.HasFatalErrors)
            {
                throw CommunicationRepositoryException.CommonOperationFailed(operationResult);
            }

            await session.CommitTransactionAsync();
        }
        catch (CommunicationRepositoryException)
        {
            throw;
        }
        catch (Exception e)
        {
            throw CommunicationRepositoryException.CommonFailedSetAdapterConfigurationState(tenantId, adapterRtEntityId,
                configurationState, e);
        }
    }

    #region Pipeline Execution

    public async Task CreatePipelineExecutionAsync(string tenantId, RtPipelineExecution execution,
        RtEntityId pipelineRtEntityId, RtEntityId adapterRtEntityId)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        using var session = await tenantRepository.GetSessionAsync();
        try
        {
            session.StartTransaction();

            var entityUpdateInfoList = new List<EntityUpdateInfo<RtPipelineExecution>>
            {
                EntityUpdateInfo<RtPipelineExecution>.CreateInsert(execution)
            };

            // Create associations to Pipeline and Adapter - must be created together with entity
            // due to minimum multiplicity constraint (One)
            var associations = new List<AssociationUpdateInfo>
            {
                AssociationUpdateInfo.CreateInsert(
                    execution.ToRtEntityId(),
                    pipelineRtEntityId,
                    SystemCommunicationCkIds.RtCkExecutedPipelineRoleId),
                AssociationUpdateInfo.CreateInsert(
                    execution.ToRtEntityId(),
                    adapterRtEntityId,
                    SystemCommunicationCkIds.RtCkExecutingAdapterRoleId)
            };

            // Apply entity and associations together to satisfy minimum multiplicity constraint
            OperationResult operationResult = new();
            await tenantRepository.ApplyChangesAsync(session, entityUpdateInfoList, associations, operationResult);
            if (operationResult.HasErrors || operationResult.HasFatalErrors)
            {
                throw CommunicationRepositoryException.CommonOperationFailed(operationResult);
            }

            await session.CommitTransactionAsync();
        }
        catch (CommunicationRepositoryException)
        {
            throw;
        }
        catch (Exception e)
        {
            throw CommunicationRepositoryException.CommonFailedCreatePipelineExecution(tenantId, execution.ExecutionId, e);
        }
    }

    public async Task UpdatePipelineExecutionAsync(string tenantId, string executionId,
        RtPipelineExecutionStatusEnum status, DateTime? completedAt, int? durationMs, string? errorMessage,
        string? outputData = null)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        using var session = await tenantRepository.GetSessionAsync();
        try
        {
            session.StartTransaction();

            // Find the execution by ExecutionId field
            var queryOptions = RtEntityQueryOptions.Create()
                .FieldFilter(nameof(RtPipelineExecution.ExecutionId), FieldFilterOperator.Equals, executionId);

            var resultSet = await tenantRepository.GetRtEntitiesByTypeAsync<RtPipelineExecution>(session, queryOptions);
            var execution = resultSet.Items.FirstOrDefault();

            if (execution == null)
            {
                throw CommunicationRepositoryException.ExecutionNotFound(tenantId, executionId);
            }

            var updatedExecution = new RtPipelineExecution
            {
                Status = status,
                CompletedAt = completedAt,
                DurationMs = durationMs,
                ErrorMessage = errorMessage,
                OutputData = outputData
            };

            var entityUpdateInfoList = new List<EntityUpdateInfo<RtPipelineExecution>>
            {
                EntityUpdateInfo<RtPipelineExecution>.CreateUpdate(execution.ToRtEntityId(), updatedExecution)
            };

            OperationResult operationResult = new();
            await tenantRepository.ApplyChangesAsync(session, entityUpdateInfoList, operationResult);
            if (operationResult.HasErrors || operationResult.HasFatalErrors)
            {
                throw CommunicationRepositoryException.CommonOperationFailed(operationResult);
            }

            await session.CommitTransactionAsync();
        }
        catch (CommunicationRepositoryException)
        {
            throw;
        }
        catch (Exception e)
        {
            throw CommunicationRepositoryException.CommonFailedUpdatePipelineExecution(tenantId, executionId, e);
        }
    }

    public async Task<int> BulkUpdatePipelineExecutionsAsync(string tenantId,
        IReadOnlyList<Models.PipelineExecutionUpdate> updates)
    {
        if (updates.Count == 0)
        {
            return 0;
        }

        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        using var session = await tenantRepository.GetSessionAsync();
        try
        {
            session.StartTransaction();

            // Query all executions at once using IN filter
            var executionIds = updates.Select(u => u.ExecutionId).ToList();
            var queryOptions = RtEntityQueryOptions.Create()
                .FieldFilter(nameof(RtPipelineExecution.ExecutionId), FieldFilterOperator.In, executionIds);

            var resultSet = await tenantRepository.GetRtEntitiesByTypeAsync<RtPipelineExecution>(session, queryOptions);
            var executionMap = resultSet.Items
                .Where(e => e.ExecutionId != null)
                .ToDictionary(e => e.ExecutionId!);

            // Build all update operations
            var entityUpdateInfoList = new List<EntityUpdateInfo<RtPipelineExecution>>();
            foreach (var update in updates)
            {
                if (!executionMap.TryGetValue(update.ExecutionId, out var execution))
                {
                    continue;
                }

                var updatedExecution = new RtPipelineExecution
                {
                    Status = update.Status,
                    CompletedAt = update.CompletedAt,
                    DurationMs = update.DurationMs,
                    ErrorMessage = update.ErrorMessage,
                    OutputData = update.OutputData
                };

                entityUpdateInfoList.Add(
                    EntityUpdateInfo<RtPipelineExecution>.CreateUpdate(execution.ToRtEntityId(), updatedExecution));
            }

            if (entityUpdateInfoList.Count == 0)
            {
                await session.CommitTransactionAsync();
                return 0;
            }

            // Apply all updates in one call
            OperationResult operationResult = new();
            await tenantRepository.ApplyChangesAsync(session, entityUpdateInfoList, operationResult);
            if (operationResult.HasErrors || operationResult.HasFatalErrors)
            {
                throw CommunicationRepositoryException.CommonOperationFailed(operationResult);
            }

            await session.CommitTransactionAsync();
            return entityUpdateInfoList.Count;
        }
        catch (CommunicationRepositoryException)
        {
            throw;
        }
        catch (Exception e)
        {
            throw CommunicationRepositoryException.CommonFailedBulkUpdateExecutions(tenantId, e);
        }
    }

    public async Task<RtPipelineExecution?> GetPipelineExecutionAsync(string tenantId, string executionId)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        using var session = await tenantRepository.GetSessionAsync();
        try
        {
            var queryOptions = RtEntityQueryOptions.Create()
                .FieldFilter(nameof(RtPipelineExecution.ExecutionId), FieldFilterOperator.Equals, executionId);

            var resultSet = await tenantRepository.GetRtEntitiesByTypeAsync<RtPipelineExecution>(session, queryOptions);

            return resultSet.Items.FirstOrDefault();
        }
        catch (Exception e)
        {
            throw CommunicationRepositoryException.CommonFailedGetPipelineExecution(tenantId, executionId, e);
        }
    }

    public async Task<IReadOnlyList<RtPipelineExecution>> GetPipelineExecutionsAsync(string tenantId,
        RtEntityId pipelineRtEntityId, DateTime? from, DateTime? to, int? limit)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        using var session = await tenantRepository.GetSessionAsync();
        try
        {
            var queryOptions = RtEntityQueryOptions.Create()
                .SortOrder(nameof(RtPipelineExecution.StartedAt), SortOrders.Descending);

            if (from.HasValue)
            {
                queryOptions.FieldFilter(nameof(RtPipelineExecution.StartedAt), FieldFilterOperator.GreaterEqualThan, from.Value);
            }

            if (to.HasValue)
            {
                queryOptions.FieldFilter(nameof(RtPipelineExecution.StartedAt), FieldFilterOperator.LessEqualThan, to.Value);
            }

            var resultSet = await tenantRepository.GetRtAssociationTargetsAsync<RtPipeline, RtPipelineExecution>(
                session,
                [pipelineRtEntityId.RtId],
                SystemCommunicationCkIds.RtCkExecutedPipelineRoleId,
                GraphDirections.Inbound,
                null,
                queryOptions,
                take: limit);

            if (resultSet.Any())
            {
                return resultSet.First().Value.Items.ToList();
            }

            return [];
        }
        catch (Exception e)
        {
            throw CommunicationRepositoryException.CommonFailedGetPipelineExecutions(tenantId, pipelineRtEntityId, e);
        }
    }

    public async Task<IReadOnlyList<RtPipelineExecution>> GetPipelineExecutionsAsync(string tenantId,
        RtEntityId pipelineRtEntityId, DateTime? from, DateTime? to, int skip, int take)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        using var session = await tenantRepository.GetSessionAsync();
        try
        {
            var queryOptions = RtEntityQueryOptions.Create()
                .SortOrder(nameof(RtPipelineExecution.StartedAt), SortOrders.Descending);

            if (from.HasValue)
            {
                queryOptions.FieldFilter(nameof(RtPipelineExecution.StartedAt), FieldFilterOperator.GreaterEqualThan, from.Value);
            }

            if (to.HasValue)
            {
                queryOptions.FieldFilter(nameof(RtPipelineExecution.StartedAt), FieldFilterOperator.LessEqualThan, to.Value);
            }

            var resultSet = await tenantRepository.GetRtAssociationTargetsAsync<RtPipeline, RtPipelineExecution>(
                session,
                [pipelineRtEntityId.RtId],
                SystemCommunicationCkIds.RtCkExecutedPipelineRoleId,
                GraphDirections.Inbound,
                null,
                queryOptions,
                skip,
                take);

            if (resultSet.Any())
            {
                return resultSet.First().Value.Items.ToList();
            }

            return [];
        }
        catch (Exception e)
        {
            throw CommunicationRepositoryException.CommonFailedGetPipelineExecutions(tenantId, pipelineRtEntityId, e);
        }
    }

    public async Task<IReadOnlyList<RtPipelineExecution>> GetTerminalExecutionsOlderThanAsync(string tenantId,
        RtEntityId pipelineRtEntityId, DateTime olderThan, int take)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        using var session = await tenantRepository.GetSessionAsync();
        try
        {
            // Oldest first so repeated drain batches make monotonic progress. Running executions
            // are excluded — they are folded/pruned only after they reach a terminal state.
            //
            // 🔴 AB#4924: Queued is named EXPLICITLY, as defence in depth — measured, not assumed.
            // This filter is currently redundant, and the reason it is redundant is a MongoDB
            // subtlety rather than anything in this file: a queued execution has no StartedAt, and
            // MongoDB's range operators are TYPE-BRACKETED, so `StartedAt < olderThan` does not
            // match a null or missing value even though null sorts below every date. Verified by
            // mutation: removing this clause leaves
            // FailStuckAndOrphanedExecutionsTests.AQueuedExecutionIsInvisibleToEveryReaperAndSweep
            // green.
            //
            // It stays because of what is downstream. PipelineStatisticsFolder skips an execution
            // without a StartedAt, but FoldAndPrunePipelineAsync then ERASES every row of the batch
            // it drained — so if that bracketing ever stopped holding, the failure mode would be
            // silent deletion of work items still waiting for a lease. This makes the invisibility a
            // property of the query rather than of the storage engine's comparison semantics.
            var queryOptions = RtEntityQueryOptions.Create()
                .SortOrder(nameof(RtPipelineExecution.StartedAt), SortOrders.Ascending)
                .FieldFilter(nameof(RtPipelineExecution.StartedAt), FieldFilterOperator.LessThan, olderThan)
                .FieldFilter(nameof(RtPipelineExecution.Status), FieldFilterOperator.NotIn,
                    new[]
                    {
                        (int)RtPipelineExecutionStatusEnum.Running,
                        (int)RtPipelineExecutionStatusEnum.Queued
                    });

            var resultSet = await tenantRepository.GetRtAssociationTargetsAsync<RtPipeline, RtPipelineExecution>(
                session,
                [pipelineRtEntityId.RtId],
                SystemCommunicationCkIds.RtCkExecutedPipelineRoleId,
                GraphDirections.Inbound,
                null,
                queryOptions,
                skip: 0,
                take: take);

            if (resultSet.Any())
            {
                return resultSet.First().Value.Items.ToList();
            }

            return [];
        }
        catch (Exception e)
        {
            throw CommunicationRepositoryException.CommonFailedGetPipelineExecutions(tenantId, pipelineRtEntityId, e);
        }
    }

    public async Task<int> DeleteExecutionsAsync(string tenantId, IReadOnlyList<RtEntityId> executionRtEntityIds)
    {
        if (executionRtEntityIds.Count == 0)
        {
            return 0;
        }

        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        try
        {
            using var session = await tenantRepository.GetSessionAsync();
            session.StartTransaction();

            // Executions are telemetry: erase for real (same rationale as DeleteOldExecutionsAsync,
            // AB#4363) — the default Archive strategy would leave tombstones in MongoDB forever.
            var entityUpdateInfoList = executionRtEntityIds
                .Select(EntityUpdateInfo<RtPipelineExecution>.CreateDelete)
                .ToList();

            OperationResult operationResult = new();
            await tenantRepository.ApplyChangesAsync(session, entityUpdateInfoList, DeleteOptions.Erase,
                operationResult);
            if (operationResult.HasErrors || operationResult.HasFatalErrors)
            {
                throw CommunicationRepositoryException.CommonOperationFailed(operationResult);
            }

            await session.CommitTransactionAsync();
            return executionRtEntityIds.Count;
        }
        catch (CommunicationRepositoryException)
        {
            throw;
        }
        catch (Exception e)
        {
            throw CommunicationRepositoryException.CommonFailedDeleteOldExecutions(tenantId, DateTime.MinValue, e);
        }
    }

    public async Task<IReadOnlyList<RtPipelineExecution>> GetRunningExecutionsForAdapterAsync(string tenantId,
        RtEntityId adapterRtEntityId)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        using var session = await tenantRepository.GetSessionAsync();
        try
        {
            // Reversed query: start from Running executions (few: 0-30) instead of
            // adapter associations (many: 7K-18K), then check which belong to this adapter.
            var executions = await GetExecutionsForAdapterByStatusAsync(
                tenantRepository, session, adapterRtEntityId, RtPipelineExecutionStatusEnum.Running);

            return executions;
        }
        catch (Exception e)
        {
            throw CommunicationRepositoryException.CommonFailedGetRunningExecutions(tenantId, adapterRtEntityId, e);
        }
    }

    public async Task<IReadOnlyList<string>> GetInterruptedExecutionIdsAsync(string tenantId, RtEntityId adapterRtEntityId)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        using var session = await tenantRepository.GetSessionAsync();
        try
        {
            // Reversed query: start from Interrupted executions (few) instead of
            // adapter associations (many: 7K-18K), then check which belong to this adapter.
            var executions = await GetExecutionsForAdapterByStatusAsync(
                tenantRepository, session, adapterRtEntityId, RtPipelineExecutionStatusEnum.Interrupted);

            return executions
                .Where(e => e.ExecutionId != null)
                .Select(e => e.ExecutionId!)
                .ToList();
        }
        catch (Exception e)
        {
            throw CommunicationRepositoryException.CommonFailedGetInterruptedExecutions(tenantId, adapterRtEntityId, e);
        }
    }

    /// <summary>
    /// Gets pipeline executions for a specific adapter filtered by status using a reversed query approach.
    /// Instead of traversing from adapter (many associations) to executions, this first queries
    /// executions by status (few results), then checks which ones belong to the specified adapter.
    /// </summary>
    private static async Task<List<RtPipelineExecution>> GetExecutionsForAdapterByStatusAsync(
        ITenantRepository tenantRepository, IOctoSession session,
        RtEntityId adapterRtEntityId, RtPipelineExecutionStatusEnum status)
    {
        // Step 1: Get all executions with the desired status (typically 0-30 results)
        var statusQueryOptions = RtEntityQueryOptions.Create()
            .FieldFilter(nameof(RtPipelineExecution.Status), FieldFilterOperator.Equals, (int)status);

        var allWithStatus = await tenantRepository.GetRtEntitiesByTypeAsync<RtPipelineExecution>(session, statusQueryOptions);

        if (!allWithStatus.Items.Any())
        {
            return [];
        }

        // Step 2: For those executions, check which ones are associated with this adapter.
        // Each execution has ~2 associations, so this is very cheap (0-30 entities × 2 associations)
        // compared to the old approach (1 adapter × 7K-18K associations).
        var executionRtIds = allWithStatus.Items.Select(e => e.RtId).ToList();

        var associationResult = await tenantRepository.GetRtAssociationTargetsAsync<RtPipelineExecution, RtAdapter>(
            session,
            executionRtIds,
            SystemCommunicationCkIds.RtCkExecutingAdapterRoleId,
            GraphDirections.Outbound,
            [adapterRtEntityId.RtId],
            RtEntityQueryOptions.Create());

        // Step 3: Collect execution IDs that have the association to this adapter
        var matchingExecutionRtIds = new HashSet<OctoObjectId>();
        foreach (var entry in associationResult)
        {
            if (entry.Value.Items.Any())
            {
                matchingExecutionRtIds.Add(entry.Key.RtId);
            }
        }

        // Step 4: Return matching executions
        return allWithStatus.Items
            .Where(e => matchingExecutionRtIds.Contains(e.RtId))
            .ToList();
    }

    public async Task<int> DeleteOldExecutionsAsync(string tenantId, DateTime olderThan)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);
        const int batchSize = 100;

        try
        {
            // Executions are telemetry, not domain data: erase them for real instead of the
            // default Archive strategy, which only sets rtState=Archived and leaves the
            // documents in MongoDB forever (the collection grew to 1M+ docs per tenant).
            // includeArchived drains the tombstones accumulated by earlier archive-only runs.
            // 🔴 AB#4924: Queued is excluded explicitly. Same defence-in-depth reasoning as in
            // GetTerminalExecutionsOlderThanAsync, and this query needs it more: it has NO status
            // filter at all, so the only thing standing between a queued work item and the daily
            // retention ERASE is MongoDB's type bracketing on `StartedAt < olderThan`. That does
            // hold today (verified by mutation), and a sweep that deletes work nobody saw go is not
            // something to leave resting on it.
            //
            // The cost is that a queued execution whose pipeline was deleted is never swept: a
            // visible leak in a queue view, which is strictly better than silent work loss.
            var queryOptions = RtEntityQueryOptions.Create()
                .FieldFilter(nameof(RtPipelineExecution.StartedAt), FieldFilterOperator.LessThan, olderThan)
                .FieldFilter(nameof(RtPipelineExecution.Status), FieldFilterOperator.NotEquals,
                    (int)RtPipelineExecutionStatusEnum.Queued)
                .Global(includeArchived: true);

            var totalDeleted = 0;
            while (true)
            {
                using var session = await tenantRepository.GetSessionAsync();
                session.StartTransaction();

                var resultSet = await tenantRepository.GetRtEntitiesByTypeAsync<RtPipelineExecution>(
                    session, queryOptions, skip: 0, take: batchSize);
                var batch = resultSet.Items.ToList();

                if (batch.Count == 0)
                {
                    await session.CommitTransactionAsync();
                    break;
                }

                var entityUpdateInfoList = batch
                    .Select(e => EntityUpdateInfo<RtPipelineExecution>.CreateDelete(e.ToRtEntityId()))
                    .ToList();

                OperationResult operationResult = new();
                await tenantRepository.ApplyChangesAsync(session, entityUpdateInfoList, DeleteOptions.Erase,
                    operationResult);
                if (operationResult.HasErrors || operationResult.HasFatalErrors)
                {
                    throw CommunicationRepositoryException.CommonOperationFailed(operationResult);
                }

                await session.CommitTransactionAsync();
                totalDeleted += batch.Count;
            }

            return totalDeleted;
        }
        catch (CommunicationRepositoryException)
        {
            throw;
        }
        catch (Exception e)
        {
            throw CommunicationRepositoryException.CommonFailedDeleteOldExecutions(tenantId, olderThan, e);
        }
    }

    public async Task<int> TimeoutStaleExecutionsAsync(string tenantId, DateTime olderThan)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);
        const int batchSize = 100;

        try
        {
            var queryOptions = RtEntityQueryOptions.Create()
                .FieldFilter(nameof(RtPipelineExecution.Status), FieldFilterOperator.Equals, (int)RtPipelineExecutionStatusEnum.Running)
                .FieldFilter(nameof(RtPipelineExecution.StartedAt), FieldFilterOperator.LessThan, olderThan);

            var totalTimedOut = 0;
            while (true)
            {
                using var session = await tenantRepository.GetSessionAsync();
                session.StartTransaction();

                var resultSet = await tenantRepository.GetRtEntitiesByTypeAsync<RtPipelineExecution>(
                    session, queryOptions, skip: 0, take: batchSize);
                var batch = resultSet.Items.ToList();

                if (batch.Count == 0)
                {
                    await session.CommitTransactionAsync();
                    break;
                }

                var entityUpdateInfoList = batch
                    .Select(e =>
                    {
                        var updated = new RtPipelineExecution
                        {
                            Status = RtPipelineExecutionStatusEnum.Failed,
                            ErrorMessage = "Execution timed out",
                            CompletedAt = DateTime.UtcNow
                        };
                        return EntityUpdateInfo<RtPipelineExecution>.CreateUpdate(e.ToRtEntityId(), updated);
                    })
                    .ToList();

                OperationResult operationResult = new();
                await tenantRepository.ApplyChangesAsync(session, entityUpdateInfoList, operationResult);
                if (operationResult.HasErrors || operationResult.HasFatalErrors)
                {
                    throw CommunicationRepositoryException.CommonOperationFailed(operationResult);
                }

                await session.CommitTransactionAsync();
                totalTimedOut += batch.Count;
            }

            return totalTimedOut;
        }
        catch (CommunicationRepositoryException)
        {
            throw;
        }
        catch (Exception e)
        {
            throw CommunicationRepositoryException.CommonFailedTimeoutStaleExecutions(tenantId, olderThan, e);
        }
    }

    public async Task<int> FailStuckExecutionsAsync(string tenantId, DateTime graceCutoff)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        try
        {
            List<RtPipelineExecution> interrupted;
            List<RtPipelineExecution> offlineRunning;

            using (var session = await tenantRepository.GetSessionAsync())
            {
                // 🔴 AB#4924 — a Queued execution is invisible here, and it is worth saying WHY
                // rather than relying on a reader noticing. Both reads below filter on an EXACT
                // status (Interrupted, Running), so Queued is excluded by construction: this is the
                // half the plan calls out, and it needs no new clause. Swapping either status for
                // Queued fails
                // FailStuckAndOrphanedExecutionsTests.AQueuedExecutionIsInvisibleToEveryReaperAndSweep
                // and nothing else, which is how that was checked rather than assumed.
                //
                // The two sibling sweeps in this file — GetTerminalExecutionsOlderThanAsync and
                // DeleteOldExecutionsAsync — are safe for a different and much less obvious reason
                // (MongoDB type bracketing on a missing StartedAt) and now name Queued explicitly
                // so that they do not depend on it. See the comments there.
                //
                // Interrupted executions past the grace period imply the owning adapter disconnected
                // and never reported a final result (fresh restart or gone for good) -> orphaned.
                interrupted = await GetExecutionsByStatusOlderThanAsync(
                    tenantRepository, session, RtPipelineExecutionStatusEnum.Interrupted, graceCutoff);

                // Running executions are only orphaned when their owning adapter is NOT Online.
                // A long-running pipeline on a connected adapter must never be failed by the reaper,
                // regardless of how long it runs.
                var running = await GetExecutionsByStatusOlderThanAsync(
                    tenantRepository, session, RtPipelineExecutionStatusEnum.Running, graceCutoff);

                offlineRunning = running.Count > 0
                    ? await FilterExecutionsWithNonOnlineAdapterAsync(tenantRepository, session, running)
                    : [];
            }

            var total = 0;
            total += await ApplyFailedStatusAsync(tenantRepository, interrupted,
                "Adapter restarted; interrupted execution not recovered within grace period");
            total += await ApplyFailedStatusAsync(tenantRepository, offlineRunning,
                "Adapter offline; running execution orphaned by adapter restart");
            return total;
        }
        catch (CommunicationRepositoryException)
        {
            throw;
        }
        catch (Exception e)
        {
            throw CommunicationRepositoryException.CommonFailedTimeoutStaleExecutions(tenantId, graceCutoff, e);
        }
    }

    public async Task<int> FailOrphanedExecutionsForAdapterAsync(string tenantId, RtEntityId adapterRtEntityId,
        DateTime beforeUtc)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        try
        {
            List<RtPipelineExecution> orphaned;
            using (var session = await tenantRepository.GetSessionAsync())
            {
                // A freshly (re)started adapter process cannot own any execution that started before
                // it began. Any Running / Interrupted execution for this adapter with an earlier
                // StartedAt is therefore an orphan left behind by the previous process -> fail it.
                var running = await GetExecutionsForAdapterByStatusAsync(
                    tenantRepository, session, adapterRtEntityId, RtPipelineExecutionStatusEnum.Running);
                var interrupted = await GetExecutionsForAdapterByStatusAsync(
                    tenantRepository, session, adapterRtEntityId, RtPipelineExecutionStatusEnum.Interrupted);

                orphaned = running.Concat(interrupted)
                    .Where(e => e.StartedAt < beforeUtc)
                    .ToList();
            }

            return await ApplyFailedStatusAsync(tenantRepository, orphaned, "Execution orphaned by adapter restart");
        }
        catch (CommunicationRepositoryException)
        {
            throw;
        }
        catch (Exception e)
        {
            throw CommunicationRepositoryException.CommonFailedTimeoutStaleExecutions(tenantId, beforeUtc, e);
        }
    }

    #region Adapter pool queue (AB#4924 increment 7)

    /// <inheritdoc />
    public async Task EnqueueExecutionAsync(string tenantId, RtPipelineExecution execution,
        RtEntityId pipelineRtEntityId, RtEntityId adapterRtEntityId, DateTime queuedAtUtc)
    {
        // 🔴 Status and QueuedAt are written HERE rather than by the caller, so there is exactly one
        // place in the controller that can produce a Queued execution and exactly one place that
        // decides a queued execution has no StartedAt. A caller that forgot either would produce an
        // entity the scheduler never picks up, or one the AB#4280 reaper ages out.
        execution.Status = RtPipelineExecutionStatusEnum.Queued;
        execution.QueuedAt = queuedAtUtc;
        execution.StartedAt = null;

        await CreatePipelineExecutionAsync(tenantId, execution, pipelineRtEntityId, adapterRtEntityId);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<QueuedExecution>> GetQueuedExecutionsForAdapterAsync(string tenantId,
        RtEntityId adapterRtEntityId, int take)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        using var session = await tenantRepository.GetSessionAsync();
        try
        {
            var queued = await ReadQueuedExecutionEntitiesAsync(tenantRepository, session, take);
            if (queued.Count == 0)
            {
                return [];
            }

            // Reversed query, for the same reason GetExecutionsForAdapterByStatusAsync uses one:
            // queued executions are few, an adapter's association fan-out is not.
            var associationResult = await tenantRepository.GetRtAssociationTargetsAsync<RtPipelineExecution, RtAdapter>(
                session,
                queued.Select(e => e.RtId).ToList(),
                SystemCommunicationCkIds.RtCkExecutingAdapterRoleId,
                GraphDirections.Outbound,
                [adapterRtEntityId.RtId],
                RtEntityQueryOptions.Create());

            var forThisAdapter = new HashSet<OctoObjectId>();
            foreach (var entry in associationResult)
            {
                if (entry.Value.Items.Any())
                {
                    forThisAdapter.Add(entry.Key.RtId);
                }
            }

            var mine = queued.Where(e => forThisAdapter.Contains(e.RtId)).ToList();
            return await ProjectQueuedExecutionsAsync(tenantRepository, session, mine);
        }
        catch (Exception e)
        {
            throw CommunicationRepositoryException.CommonFailedGetRunningExecutions(tenantId, adapterRtEntityId, e);
        }
    }

    /// <inheritdoc />
    public async Task<int> GetQueuedExecutionPositionAsync(string tenantId, string executionId)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        using var session = await tenantRepository.GetSessionAsync();
        try
        {
            var queued = await ReadQueuedExecutionEntitiesAsync(tenantRepository, session, MaxQueueReadBatch);
            if (queued.Count == 0)
            {
                return 0;
            }

            // 🔴 Ordered the way the SCHEDULER orders, not merely by QueuedAt. A position computed
            // from arrival time alone would tell an Interactive job it is tenth when it is about to
            // run first — a number that contradicts the behaviour is worse than no number.
            var ordered = (await ProjectQueuedExecutionsAsync(tenantRepository, session, queued))
                .Order(QueuedExecution.SchedulingOrder)
                .ToList();

            var index = ordered.FindIndex(e => string.Equals(e.ExecutionId, executionId, StringComparison.Ordinal));
            return index < 0 ? 0 : index + 1;
        }
        catch (Exception e)
        {
            throw CommunicationRepositoryException.CommonFailedGetPipelineExecution(tenantId, executionId, e);
        }
    }

    /// <inheritdoc />
    public async Task<bool> TryClaimQueuedExecutionAsync(string tenantId, string executionId, LeaseClaim claim)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        try
        {
            RtPipelineExecution? execution;
            using (var readSession = await tenantRepository.GetSessionAsync())
            {
                execution = await FindExecutionAsync(tenantRepository, readSession, executionId);
            }

            if (execution is null || execution.Status != RtPipelineExecutionStatusEnum.Queued)
            {
                return false;
            }

            var queuedAt = execution.QueuedAt ?? claim.GrantedAtUtc;
            var waitMs = (int)Math.Max(0, Math.Round((claim.GrantedAtUtc - queuedAt).TotalMilliseconds));

            var claimed = new RtPipelineExecution
            {
                Status = RtPipelineExecutionStatusEnum.Running,
                // StartedAt and LeaseGrantedAt are the same instant but NOT the same span: StartedAt
                // is what every existing execution query and both AB#4280 reapers key off, and
                // LeaseGrantedAt is one end of the lease-held span that prices the borrower
                // (concept §2.3 / §4b). Writing only one of them would break one of the two.
                StartedAt = claim.GrantedAtUtc,
                LeaseGrantedAt = claim.GrantedAtUtc,
                LeaseWaitMs = waitMs,
                LeasedFromTenantId = claim.LenderTenantId,
                LeasedFromAdapterPoolRtId = claim.AdapterPoolRtId,
                LeasedOnMemberId = claim.MemberId
            };

            // 🔴 The claim latch. The guard applies the write only while the persisted
            // leaseGrantedAt is missing, null or <= the guard value; DateTime.MinValue is therefore
            // "not claimed yet", because any real grant time is greater. Two controller pods — each
            // holding different members of the same pool — can reach this line for the same work
            // item, and without the latch both would dispatch it. MongoDB decides, at the filter
            // level, which one wins.
            var guard = new AttributeNewerThanGuard("attributes.leaseGrantedAt", DateTime.MinValue);

            using (var session = await tenantRepository.GetSessionAsync())
            {
                session.StartTransaction();

                OperationResult operationResult = new();
                await tenantRepository.ApplyChangesAsync(session,
                    new List<EntityUpdateInfo<RtPipelineExecution>>
                    {
                        EntityUpdateInfo<RtPipelineExecution>.CreateConditionalUpdate(execution.ToRtEntityId(),
                            claimed, guard)
                    },
                    operationResult);
                if (operationResult.HasErrors || operationResult.HasFatalErrors)
                {
                    throw CommunicationRepositoryException.CommonOperationFailed(operationResult);
                }

                await session.CommitTransactionAsync();
            }

            // A conditional update reports nothing about whether it applied, so the only way to know
            // is to look. Cheap, and it is the one read that decides whether a member was handed
            // work somebody else is already running.
            using (var verifySession = await tenantRepository.GetSessionAsync())
            {
                var reloaded = await FindExecutionAsync(tenantRepository, verifySession, executionId);
                return reloaded is not null
                       && reloaded.Status == RtPipelineExecutionStatusEnum.Running
                       && string.Equals(reloaded.LeasedOnMemberId, claim.MemberId, StringComparison.Ordinal);
            }
        }
        catch (CommunicationRepositoryException)
        {
            throw;
        }
        catch (Exception e)
        {
            throw CommunicationRepositoryException.CommonFailedUpdatePipelineExecution(tenantId, executionId, e);
        }
    }

    /// <inheritdoc />
    public async Task<bool> TryCancelQueuedExecutionAsync(string tenantId, string executionId, string? reason)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        using var session = await tenantRepository.GetSessionAsync();
        try
        {
            session.StartTransaction();

            var execution = await FindExecutionAsync(tenantRepository, session, executionId);
            if (execution is null || execution.Status != RtPipelineExecutionStatusEnum.Queued)
            {
                // 🔴 Not an error, and deliberately not a status transition either. An execution that
                // already HOLDS a lease is cancelled by interrupting the running pipeline, which is
                // the existing cancellation path; collapsing the two here would make "cancel" mean
                // two different things depending on a race (concept §5, "Cancellation").
                await session.CommitTransactionAsync();
                return false;
            }

            var cancelled = new RtPipelineExecution
            {
                Status = RtPipelineExecutionStatusEnum.Cancelled,
                CompletedAt = DateTime.UtcNow,
                // Never started, so never took any time. 0 rather than a duration measured from a
                // substituted start timestamp.
                DurationMs = 0,
                ErrorMessage = string.IsNullOrWhiteSpace(reason)
                    ? "Cancelled while waiting for an adapter pool lease."
                    : reason
            };

            OperationResult operationResult = new();
            await tenantRepository.ApplyChangesAsync(session,
                new List<EntityUpdateInfo<RtPipelineExecution>>
                {
                    EntityUpdateInfo<RtPipelineExecution>.CreateUpdate(execution.ToRtEntityId(), cancelled)
                },
                operationResult);
            if (operationResult.HasErrors || operationResult.HasFatalErrors)
            {
                throw CommunicationRepositoryException.CommonOperationFailed(operationResult);
            }

            await session.CommitTransactionAsync();
            return true;
        }
        catch (CommunicationRepositoryException)
        {
            throw;
        }
        catch (Exception e)
        {
            throw CommunicationRepositoryException.CommonFailedUpdatePipelineExecution(tenantId, executionId, e);
        }
    }

    /// <inheritdoc />
    public async Task StampLeaseReleasedAsync(string tenantId, string executionId, DateTime releasedAtUtc)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        using var session = await tenantRepository.GetSessionAsync();
        try
        {
            session.StartTransaction();

            var execution = await FindExecutionAsync(tenantRepository, session, executionId);
            if (execution is null)
            {
                await session.CommitTransactionAsync();
                return;
            }

            OperationResult operationResult = new();
            await tenantRepository.ApplyChangesAsync(session,
                new List<EntityUpdateInfo<RtPipelineExecution>>
                {
                    EntityUpdateInfo<RtPipelineExecution>.CreateUpdate(execution.ToRtEntityId(),
                        new RtPipelineExecution { LeaseReleasedAt = releasedAtUtc })
                },
                operationResult);
            if (operationResult.HasErrors || operationResult.HasFatalErrors)
            {
                throw CommunicationRepositoryException.CommonOperationFailed(operationResult);
            }

            await session.CommitTransactionAsync();
        }
        catch (CommunicationRepositoryException)
        {
            throw;
        }
        catch (Exception e)
        {
            throw CommunicationRepositoryException.CommonFailedUpdatePipelineExecution(tenantId, executionId, e);
        }
    }

    /// <inheritdoc />
    public async Task<InterruptedLeasedExecution?> TryInterruptLeasedExecutionAsync(string tenantId,
        string executionId, DateTime releasedAtUtc, string reason)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        using var session = await tenantRepository.GetSessionAsync();
        try
        {
            session.StartTransaction();

            var execution = await FindExecutionAsync(tenantRepository, session, executionId);
            if (execution is null || execution.Status is RtPipelineExecutionStatusEnum.Completed
                    or RtPipelineExecutionStatusEnum.Failed or RtPipelineExecutionStatusEnum.Cancelled)
            {
                await session.CommitTransactionAsync();
                return null;
            }

            var interrupted = new RtPipelineExecution
            {
                Status = RtPipelineExecutionStatusEnum.Interrupted,
                ErrorMessage = reason,
                // 🔴 Stamped on the failure path too. The member really was held for this span and
                // the borrower really is charged for it (concept §4b); a LeaseReleasedAt written
                // only when everything went well under-bills precisely the incidents.
                LeaseReleasedAt = releasedAtUtc
            };

            OperationResult operationResult = new();
            await tenantRepository.ApplyChangesAsync(session,
                new List<EntityUpdateInfo<RtPipelineExecution>>
                {
                    EntityUpdateInfo<RtPipelineExecution>.CreateUpdate(execution.ToRtEntityId(), interrupted)
                },
                operationResult);
            if (operationResult.HasErrors || operationResult.HasFatalErrors)
            {
                throw CommunicationRepositoryException.CommonOperationFailed(operationResult);
            }

            await session.CommitTransactionAsync();

            var pipeline = await ReadSinglePipelineOfExecutionAsync(tenantRepository, execution.RtId);
            var adapter = await ReadSingleAdapterOfExecutionAsync(tenantRepository, execution.RtId);

            if (pipeline is null || adapter is null)
            {
                // The attempt is recorded as interrupted either way; what cannot be done without
                // both edges is enqueue a replacement, and inventing one would be worse than telling
                // the caller there is nothing to re-queue.
                _logger.LogWarning(
                    "[{TenantId}] Interrupted leased execution '{ExecutionId}' cannot be re-queued: pipeline={HasPipeline}, adapter={HasAdapter}",
                    tenantId, executionId, pipeline is not null, adapter is not null);
                return null;
            }

            return new InterruptedLeasedExecution(
                new RtEntityId(pipeline.CkTypeId ?? SystemCommunicationCkIds.RtCkPipelineTypeId, pipeline.RtId),
                new RtEntityId(adapter.CkTypeId ?? SystemCommunicationCkIds.RtCkAdapterTypeId, adapter.RtId),
                execution.TriggerType,
                execution.InputData);
        }
        catch (CommunicationRepositoryException)
        {
            throw;
        }
        catch (Exception e)
        {
            throw CommunicationRepositoryException.CommonFailedUpdatePipelineExecution(tenantId, executionId, e);
        }
    }

    /// <inheritdoc />
    public async Task<QueuedExecution?> GetExecutionQueueEntryAsync(string tenantId, string executionId)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        using var session = await tenantRepository.GetSessionAsync();
        try
        {
            var execution = await FindExecutionAsync(tenantRepository, session, executionId);
            if (execution is null)
            {
                return null;
            }

            var projected = await ProjectQueuedExecutionsAsync(tenantRepository, session, [execution]);
            return projected.FirstOrDefault();
        }
        catch (Exception e)
        {
            throw CommunicationRepositoryException.CommonFailedGetPipelineExecution(tenantId, executionId, e);
        }
    }

    /// <summary>
    /// Upper bound on one queue read. A pool queue that is longer than this is already an incident
    /// the scale-up policy and the surfaces are shouting about; reading further would only make the
    /// scheduling round slower without changing which work item goes next.
    /// </summary>
    private const int MaxQueueReadBatch = 1000;

    /// <summary>
    /// Every execution of the tenant that is waiting for a lease, oldest first.
    /// </summary>
    private static async Task<List<RtPipelineExecution>> ReadQueuedExecutionEntitiesAsync(
        ITenantRepository tenantRepository, IOctoSession session, int take)
    {
        // Ordered by QueuedAt, which is the index 4.0.0 added for exactly this read. The StartedAt
        // index is useless here: a queued execution has no StartedAt at all, so every one of them
        // sits in that index's null bucket in arrival-independent order.
        var queryOptions = RtEntityQueryOptions.Create()
            .SortOrder(nameof(RtPipelineExecution.QueuedAt), SortOrders.Ascending)
            .FieldFilter(nameof(RtPipelineExecution.Status), FieldFilterOperator.Equals,
                (int)RtPipelineExecutionStatusEnum.Queued);

        var resultSet = await tenantRepository.GetRtEntitiesByTypeAsync<RtPipelineExecution>(session, queryOptions,
            skip: 0, take: Math.Clamp(take, 1, MaxQueueReadBatch));
        return resultSet.Items.ToList();
    }

    /// <summary>
    /// Attaches each queued execution's pipeline and that pipeline's <c>ExecutionClass</c>.
    /// </summary>
    private static async Task<List<QueuedExecution>> ProjectQueuedExecutionsAsync(
        ITenantRepository tenantRepository, IOctoSession session, IReadOnlyList<RtPipelineExecution> executions)
    {
        if (executions.Count == 0)
        {
            return [];
        }

        var associationResult = await tenantRepository.GetRtAssociationTargetsAsync<RtPipelineExecution, RtPipeline>(
            session,
            executions.Select(e => e.RtId).ToList(),
            SystemCommunicationCkIds.RtCkExecutedPipelineRoleId,
            GraphDirections.Outbound,
            null,
            RtEntityQueryOptions.Create());

        var pipelineByExecution = new Dictionary<OctoObjectId, RtPipeline>();
        foreach (var entry in associationResult)
        {
            var pipeline = entry.Value.Items.FirstOrDefault();
            if (pipeline != null)
            {
                pipelineByExecution[entry.Key.RtId] = pipeline;
            }
        }

        var projected = new List<QueuedExecution>(executions.Count);
        foreach (var execution in executions)
        {
            if (execution.ExecutionId is null)
            {
                continue;
            }

            pipelineByExecution.TryGetValue(execution.RtId, out var pipeline);

            // A pipeline that cannot be resolved degrades to Batch, the same conservative answer
            // IPipelineExecutionClassService gives for a definition nobody could classify: an
            // unknown job must never jump a queue.
            var executionClass = pipeline is null
                ? QueuedExecution.BatchClass
                : (int)pipeline.ExecutionClass;

            projected.Add(new QueuedExecution(
                execution.ExecutionId,
                execution.RtId,
                execution.QueuedAt ?? DateTime.MinValue,
                pipeline?.RtId,
                pipeline?.Name,
                executionClass,
                // AB#4924 §9.9 / D4: the input travels to the member on the lease. Read from the
                // entity, never re-derived - a retry and its original attempt must run the same input.
                execution.InputData));
        }

        return projected;
    }

    /// <summary>
    /// Finds one execution by its business <c>ExecutionId</c>, or null.
    /// </summary>
    private static async Task<RtPipelineExecution?> FindExecutionAsync(ITenantRepository tenantRepository,
        IOctoSession session, string executionId)
    {
        var queryOptions = RtEntityQueryOptions.Create()
            .FieldFilter(nameof(RtPipelineExecution.ExecutionId), FieldFilterOperator.Equals, executionId);

        var resultSet = await tenantRepository.GetRtEntitiesByTypeAsync<RtPipelineExecution>(session, queryOptions);
        return resultSet.Items.FirstOrDefault();
    }

    /// <summary>
    /// Reads the pipeline one execution belongs to, or null.
    /// </summary>
    private static async Task<RtPipeline?> ReadSinglePipelineOfExecutionAsync(ITenantRepository tenantRepository,
        OctoObjectId executionRtId)
    {
        using var session = await tenantRepository.GetSessionAsync();
        var result = await tenantRepository.GetRtAssociationTargetsAsync<RtPipelineExecution, RtPipeline>(
            session, [executionRtId], SystemCommunicationCkIds.RtCkExecutedPipelineRoleId,
            GraphDirections.Outbound, null, RtEntityQueryOptions.Create());

        return result.Count == 0 ? null : result.First().Value.Items.FirstOrDefault();
    }

    /// <summary>
    /// Reads the borrower's own adapter one execution belongs to, or null.
    /// </summary>
    private static async Task<RtAdapter?> ReadSingleAdapterOfExecutionAsync(ITenantRepository tenantRepository,
        OctoObjectId executionRtId)
    {
        using var session = await tenantRepository.GetSessionAsync();
        var result = await tenantRepository.GetRtAssociationTargetsAsync<RtPipelineExecution, RtAdapter>(
            session, [executionRtId], SystemCommunicationCkIds.RtCkExecutingAdapterRoleId,
            GraphDirections.Outbound, null, RtEntityQueryOptions.Create());

        return result.Count == 0 ? null : result.First().Value.Items.FirstOrDefault();
    }

    #endregion


    private static async Task<List<RtPipelineExecution>> GetExecutionsByStatusOlderThanAsync(
        ITenantRepository tenantRepository, IOctoSession session,
        RtPipelineExecutionStatusEnum status, DateTime olderThan)
    {
        var queryOptions = RtEntityQueryOptions.Create()
            .FieldFilter(nameof(RtPipelineExecution.Status), FieldFilterOperator.Equals, (int)status)
            .FieldFilter(nameof(RtPipelineExecution.StartedAt), FieldFilterOperator.LessThan, olderThan);

        var result = await tenantRepository.GetRtEntitiesByTypeAsync<RtPipelineExecution>(session, queryOptions);
        return result.Items.ToList();
    }

    /// <summary>
    /// Given a set of executions, returns those whose owning adapter is not <c>Online</c>
    /// (or has no resolvable adapter). Used by the connection-aware reaper so that Running
    /// executions on a live adapter are never treated as stuck.
    /// </summary>
    private static async Task<List<RtPipelineExecution>> FilterExecutionsWithNonOnlineAdapterAsync(
        ITenantRepository tenantRepository, IOctoSession session, IReadOnlyList<RtPipelineExecution> executions)
    {
        var executionRtIds = executions.Select(e => e.RtId).ToList();

        var associationResult = await tenantRepository.GetRtAssociationTargetsAsync<RtPipelineExecution, RtAdapter>(
            session,
            executionRtIds,
            SystemCommunicationCkIds.RtCkExecutingAdapterRoleId,
            GraphDirections.Outbound,
            null,
            RtEntityQueryOptions.Create());

        // Map execution RtId -> owning adapter (executions have exactly one ExecutingAdapter).
        var adapterByExecution = new Dictionary<OctoObjectId, RtAdapter>();
        foreach (var entry in associationResult)
        {
            var adapter = entry.Value.Items.FirstOrDefault();
            if (adapter != null)
            {
                adapterByExecution[entry.Key.RtId] = adapter;
            }
        }

        return executions
            .Where(e => !adapterByExecution.TryGetValue(e.RtId, out var adapter)
                        || adapter.CommunicationState != RtCommunicationStateEnum.Online)
            .ToList();
    }

    /// <summary>
    /// Marks the given executions as <c>Failed</c> in batches, stamping the error message,
    /// completion time and duration. No-op for an empty list.
    /// </summary>
    private async Task<int> ApplyFailedStatusAsync(ITenantRepository tenantRepository,
        IReadOnlyList<RtPipelineExecution> executions, string errorMessage)
    {
        if (executions.Count == 0)
        {
            return 0;
        }

        const int batchSize = 100;
        var now = DateTime.UtcNow;
        var total = 0;

        for (var offset = 0; offset < executions.Count; offset += batchSize)
        {
            var batch = executions.Skip(offset).Take(batchSize).ToList();

            using var session = await tenantRepository.GetSessionAsync();
            session.StartTransaction();

            var entityUpdateInfoList = batch
                .Select(e =>
                {
                    var updated = new RtPipelineExecution
                    {
                        Status = RtPipelineExecutionStatusEnum.Failed,
                        ErrorMessage = errorMessage,
                        CompletedAt = now,
                        // AB#4924: StartedAt is optional since 4.0.0. An execution failed
                        // without ever having started has no duration — report 0 rather
                        // than measuring from a substituted timestamp, which would show up
                        // as a fabricated runtime in the statistics.
                        DurationMs = e.StartedAt is { } startedAt
                            ? (int)Math.Max(0, (now - startedAt).TotalMilliseconds)
                            : 0
                    };
                    return EntityUpdateInfo<RtPipelineExecution>.CreateUpdate(e.ToRtEntityId(), updated);
                })
                .ToList();

            OperationResult operationResult = new();
            await tenantRepository.ApplyChangesAsync(session, entityUpdateInfoList, operationResult);
            if (operationResult.HasErrors || operationResult.HasFatalErrors)
            {
                throw CommunicationRepositoryException.CommonOperationFailed(operationResult);
            }

            await session.CommitTransactionAsync();
            total += batch.Count;
        }

        return total;
    }

    #endregion

    #region Pipeline Statistics

    public async Task<RtPipelineStatistics?> GetPipelineStatisticsAsync(string tenantId, RtEntityId pipelineRtEntityId)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        using var session = await tenantRepository.GetSessionAsync();
        try
        {
            var queryOptions = RtEntityQueryOptions.Create();

            var resultSet = await tenantRepository.GetRtAssociationTargetsAsync<RtPipeline, RtPipelineStatistics>(
                session,
                [pipelineRtEntityId.RtId],
                SystemCommunicationCkIds.RtCkStatisticsForPipelineRoleId,
                GraphDirections.Inbound,
                null,
                queryOptions);

            if (resultSet.Any())
            {
                return resultSet.First().Value.Items.FirstOrDefault();
            }

            return null;
        }
        catch (Exception e)
        {
            throw CommunicationRepositoryException.CommonFailedGetPipelineStatistics(tenantId, pipelineRtEntityId, e);
        }
    }

    public async Task UpsertPipelineStatisticsAsync(string tenantId, RtPipelineStatistics statistics,
        RtEntityId pipelineRtEntityId)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        using var session = await tenantRepository.GetSessionAsync();
        try
        {
            session.StartTransaction();

            // Check if statistics already exist
            var existing = await GetPipelineStatisticsAsync(tenantId, pipelineRtEntityId);

            OperationResult operationResult = new();

            if (existing == null)
            {
                // Insert new statistics
                var entityUpdateInfoList = new List<EntityUpdateInfo<RtPipelineStatistics>>
                {
                    EntityUpdateInfo<RtPipelineStatistics>.CreateInsert(statistics)
                };

                await tenantRepository.ApplyChangesAsync(session, entityUpdateInfoList, operationResult);
                if (operationResult.HasErrors || operationResult.HasFatalErrors)
                {
                    throw CommunicationRepositoryException.CommonOperationFailed(operationResult);
                }

                // Create association to Pipeline
                var associations = new List<AssociationUpdateInfo>
                {
                    AssociationUpdateInfo.CreateInsert(
                        statistics.ToRtEntityId(),
                        pipelineRtEntityId,
                        SystemCommunicationCkIds.RtCkStatisticsForPipelineRoleId)
                };

                await tenantRepository.ApplyChangesAsync(session, associations, operationResult);
            }
            else
            {
                // Update existing statistics
                var entityUpdateInfoList = new List<EntityUpdateInfo<RtPipelineStatistics>>
                {
                    EntityUpdateInfo<RtPipelineStatistics>.CreateUpdate(existing.ToRtEntityId(), statistics)
                };

                await tenantRepository.ApplyChangesAsync(session, entityUpdateInfoList, operationResult);
            }

            if (operationResult.HasErrors || operationResult.HasFatalErrors)
            {
                throw CommunicationRepositoryException.CommonOperationFailed(operationResult);
            }

            await session.CommitTransactionAsync();
        }
        catch (CommunicationRepositoryException)
        {
            throw;
        }
        catch (Exception e)
        {
            throw CommunicationRepositoryException.CommonFailedUpsertPipelineStatistics(tenantId, pipelineRtEntityId, e);
        }
    }

    public async Task<ExecutionAggregateResult> GetExecutionAggregateAsync(string tenantId,
        RtEntityId pipelineRtEntityId, DateTime from, DateTime to)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        using var session = await tenantRepository.GetSessionAsync();
        try
        {
            var queryOptions = RtEntityQueryOptions.Create()
                .FieldFilter(nameof(RtPipelineExecution.StartedAt), FieldFilterOperator.GreaterEqualThan, from)
                .FieldFilter(nameof(RtPipelineExecution.StartedAt), FieldFilterOperator.LessEqualThan, to);

            var resultSet = await tenantRepository.GetRtAssociationTargetsAsync<RtPipeline, RtPipelineExecution>(
                session,
                [pipelineRtEntityId.RtId],
                SystemCommunicationCkIds.RtCkExecutedPipelineRoleId,
                GraphDirections.Inbound,
                null,
                queryOptions);

            if (!resultSet.Any())
            {
                return new ExecutionAggregateResult(0, 0, 0, 0);
            }

            var executions = resultSet.First().Value.Items.ToList();

            var successCount = executions.Count(e => e.Status == RtPipelineExecutionStatusEnum.Completed);
            var failureCount = executions.Count(e => e.Status == RtPipelineExecutionStatusEnum.Failed);
            var executionsWithDuration = executions.Where(e => e.DurationMs.HasValue).ToList();
            var totalDurationMs = executionsWithDuration.Sum(e => e.DurationMs!.Value);

            return new ExecutionAggregateResult(successCount, failureCount, totalDurationMs, executionsWithDuration.Count);
        }
        catch (Exception e)
        {
            throw CommunicationRepositoryException.CommonFailedGetExecutionAggregate(tenantId, pipelineRtEntityId, e);
        }
    }

    #endregion

    #region Bulk Operations

    public async Task BulkInsertPipelineExecutionsAsync(string tenantId, IEnumerable<RtPipelineExecution> executions,
        RtEntityId pipelineRtEntityId, RtEntityId adapterRtEntityId)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        using var session = await tenantRepository.GetSessionAsync();
        try
        {
            session.StartTransaction();

            var executionList = executions.ToList();
            var entityUpdateInfoList = executionList
                .Select(EntityUpdateInfo<RtPipelineExecution>.CreateInsert)
                .ToList();

            // Create associations for all executions
            var associations = executionList.SelectMany(execution => new[]
            {
                AssociationUpdateInfo.CreateInsert(
                    execution.ToRtEntityId(),
                    pipelineRtEntityId,
                    SystemCommunicationCkIds.RtCkExecutedPipelineRoleId),
                AssociationUpdateInfo.CreateInsert(
                    execution.ToRtEntityId(),
                    adapterRtEntityId,
                    SystemCommunicationCkIds.RtCkExecutingAdapterRoleId)
            }).ToList();

            // Apply entities and associations together to satisfy minimum multiplicity constraint
            OperationResult operationResult = new();
            await tenantRepository.ApplyChangesAsync(session, entityUpdateInfoList, associations, operationResult);
            if (operationResult.HasErrors || operationResult.HasFatalErrors)
            {
                throw CommunicationRepositoryException.CommonOperationFailed(operationResult);
            }

            await session.CommitTransactionAsync();
        }
        catch (CommunicationRepositoryException)
        {
            throw;
        }
        catch (Exception e)
        {
            throw CommunicationRepositoryException.CommonFailedBulkInsertExecutions(tenantId, e);
        }
    }

    public async Task<ISet<string>> GetExistingExecutionIdsAsync(string tenantId, IEnumerable<string> executionIds)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        using var session = await tenantRepository.GetSessionAsync();
        try
        {
            var idList = executionIds.ToList();
            var queryOptions = RtEntityQueryOptions.Create()
                .FieldFilter(nameof(RtPipelineExecution.ExecutionId), FieldFilterOperator.In, idList);

            var resultSet = await tenantRepository.GetRtEntitiesByTypeAsync<RtPipelineExecution>(session, queryOptions);

            return resultSet.Items
                .Where(e => e.ExecutionId != null)
                .Select(e => e.ExecutionId!)
                .ToHashSet();
        }
        catch (Exception e)
        {
            throw CommunicationRepositoryException.CommonFailedGetExistingExecutionIds(tenantId, e);
        }
    }

    public async Task UpdateAdapterSyncSequenceNumberAsync(string tenantId, RtEntityId adapterRtEntityId, int sequenceNumber)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        using var session = await tenantRepository.GetSessionAsync();
        try
        {
            session.StartTransaction();

            var adapter = new RtAdapter
            {
                LastSyncedSequenceNumber = sequenceNumber
            };

            var entityUpdateInfoList = new List<EntityUpdateInfo<RtAdapter>>
            {
                EntityUpdateInfo<RtAdapter>.CreateUpdate(adapterRtEntityId, adapter)
            };

            OperationResult operationResult = new();
            await tenantRepository.ApplyChangesAsync(session, entityUpdateInfoList, operationResult);
            if (operationResult.HasErrors || operationResult.HasFatalErrors)
            {
                throw CommunicationRepositoryException.CommonOperationFailed(operationResult);
            }

            await session.CommitTransactionAsync();
        }
        catch (CommunicationRepositoryException)
        {
            throw;
        }
        catch (Exception e)
        {
            throw CommunicationRepositoryException.CommonFailedUpdateAdapterSyncSequenceNumber(tenantId, adapterRtEntityId, e);
        }
    }

    public async Task<int> GetAdapterSyncSequenceNumberAsync(string tenantId, RtEntityId adapterRtEntityId)
    {
        var adapter = await GetAdapterAsync(tenantId, adapterRtEntityId);
        return adapter.LastSyncedSequenceNumber;
    }

    #endregion

    #region Pipeline Queries

    public async Task<IReadOnlyCollection<RtPipeline>> GetAllPipelinesAsync(string tenantId)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        using var session = await tenantRepository.GetSessionAsync();
        try
        {
            var queryOptions = RtEntityQueryOptions.Create();
            var resultSet = await tenantRepository.GetRtEntitiesByTypeAsync<RtPipeline>(session, queryOptions);

            return resultSet.Items.ToList();
        }
        catch (Exception e)
        {
            throw CommunicationRepositoryException.CommonFailedGetAllPipelines(tenantId, e);
        }
    }

    #endregion

    #region Rights analysis (AB#5113) — System.Identity reads

    /// <inheritdoc />
    public async Task<IReadOnlyCollection<RtPipeline>> GetPipelinesUsingServiceAccountAsync(string tenantId,
        OctoObjectId serviceAccountRtId)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        using var session = await tenantRepository.GetSessionAsync();
        try
        {
            // Inbound over the generic Uses role: the edge lives on the pipeline
            // (GetConfigurationsByPipelineAsync walks it outbound), so from the configuration's
            // side it is the incoming direction. The typed target restricts the polymorphic
            // origins to pipelines.
            var resultSet = await tenantRepository
                .GetRtAssociationTargetsAsync<RtServiceAccountConfiguration, RtPipeline>(session,
                    [serviceAccountRtId], SystemCommunicationCkIds.RtCkUsesRoleId,
                    GraphDirections.Inbound, null, RtEntityQueryOptions.Create());

            if (!resultSet.Any())
            {
                return Array.Empty<RtPipeline>();
            }

            return resultSet.First().Value.Items.ToList();
        }
        catch (Exception e)
        {
            throw CommunicationRepositoryException.CommonFailedGettingPipelinesUsingServiceAccount(tenantId,
                serviceAccountRtId, e);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyCollection<RtEntity>> GetDataPoliciesAsync(string tenantId)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        using var session = await tenantRepository.GetSessionAsync();
        try
        {
            // Untyped read on purpose — no generated System.Identity model in this service, see
            // SystemIdentityCkIds. The analysis service interprets the attributes.
            var resultSet = await tenantRepository.GetRtEntitiesByTypeAsync(session,
                SystemIdentityCkIds.RtCkDataPolicyTypeId, RtEntityQueryOptions.Create());

            return resultSet.Items.ToList();
        }
        catch (Exception e)
        {
            throw CommunicationRepositoryException.CommonFailedGettingDataPolicies(tenantId, e);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<OctoObjectId, IReadOnlyList<RtEntity>>>
        GetDataPermissionsForPoliciesAsync(string tenantId, IReadOnlyCollection<OctoObjectId> policyRtIds)
    {
        if (policyRtIds.Count == 0)
        {
            return new Dictionary<OctoObjectId, IReadOnlyList<RtEntity>>();
        }

        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        using var session = await tenantRepository.GetSessionAsync();
        try
        {
            // Outbound over PolicyPermission: the edge lives on the policy.
            var resultSet = await tenantRepository.GetRtAssociationTargetsAsync(session,
                policyRtIds, SystemIdentityCkIds.RtCkDataPolicyTypeId,
                SystemIdentityCkIds.RtCkPolicyPermissionRoleId,
                SystemIdentityCkIds.RtCkDataPermissionTypeId,
                GraphDirections.Outbound, null, RtEntityQueryOptions.Create());

            return resultSet.ToDictionary(kvp => kvp.Key.RtId,
                kvp => (IReadOnlyList<RtEntity>)kvp.Value.Items.ToList());
        }
        catch (Exception e)
        {
            throw CommunicationRepositoryException.CommonFailedGettingDataPermissionsForPolicies(tenantId, e);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<OctoObjectId, IReadOnlyList<RtEntity>>>
        GetGrantingRolesForDataPermissionsAsync(string tenantId, IReadOnlyCollection<OctoObjectId> permissionRtIds)
    {
        if (permissionRtIds.Count == 0)
        {
            return new Dictionary<OctoObjectId, IReadOnlyList<RtEntity>>();
        }

        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        using var session = await tenantRepository.GetSessionAsync();
        try
        {
            // Inbound over GrantsPermission: the edge lives on the role, the permission is the
            // target — same direction reasoning as GetAdapterForServiceAccountAsync.
            var resultSet = await tenantRepository.GetRtAssociationTargetsAsync(session,
                permissionRtIds, SystemIdentityCkIds.RtCkDataPermissionTypeId,
                SystemIdentityCkIds.RtCkGrantsPermissionRoleId,
                SystemIdentityCkIds.RtCkRoleTypeId,
                GraphDirections.Inbound, null, RtEntityQueryOptions.Create());

            return resultSet.ToDictionary(kvp => kvp.Key.RtId,
                kvp => (IReadOnlyList<RtEntity>)kvp.Value.Items.ToList());
        }
        catch (Exception e)
        {
            throw CommunicationRepositoryException.CommonFailedGettingGrantingRoles(tenantId, e);
        }
    }

    #endregion

    #region Last-error tracking helpers

    /// <summary>
    /// Applies the persistent last-deployment-error policy to <paramref name="entity"/>.
    /// <para>
    /// Behaviour by target state:
    /// <list type="bullet">
    ///   <item>Error      → write LastDeploymentError = message (or "(no message)") and timestamp = now.</item>
    ///   <item>Pending    → clear both fields. A Pending transition is always driven by an active
    ///                      retry (user-triggered deploy, controller-driven redeploy, operator
    ///                      reconnect), and showing the previous failure on top of a fresh attempt
    ///                      is confusing: the user can no longer tell whether their fix actually
    ///                      changed anything. The window before a Deployed / Error round-trip
    ///                      lands is brief (seconds), so dropping the error here trades a tiny bit
    ///                      of context for "click Deploy → banner disappears → wait → see fresh
    ///                      result".</item>
    ///   <item>Deployed   → clear both fields (the deploy has succeeded; the previous failure is resolved).</item>
    ///   <item>Other      → leave fields untouched so the user keeps seeing the failure context across
    ///                      transient intermediate states like operator-driven Undeployed /
    ///                      Disabled transitions.</item>
    /// </list>
    /// </para>
    /// <para>
    /// This is the persistent counterpart to <c>StatusMessage</c>, which the existing call sites
    /// still overwrite on every state change. The split was introduced because the operator
    /// reports a successful redeploy with a null status message — silently wiping any previous
    /// error context from the UI.
    /// </para>
    /// </summary>
    private static void ApplyDeploymentErrorTracking(RtDeployableEntity entity,
        RtDeploymentStateEnum deploymentState, string? stateMessage)
    {
        if (deploymentState == RtDeploymentStateEnum.Error)
        {
            entity.LastDeploymentError = string.IsNullOrWhiteSpace(stateMessage)
                ? "(no message)"
                : stateMessage;
            entity.LastDeploymentErrorTimestamp = DateTime.UtcNow;
        }
        else if (deploymentState == RtDeploymentStateEnum.Deployed ||
                 deploymentState == RtDeploymentStateEnum.Pending)
        {
            entity.LastDeploymentError = null;
            entity.LastDeploymentErrorTimestamp = null;
        }
    }

    /// <summary>
    /// Mirrors <see cref="ApplyDeploymentErrorTracking"/> for an adapter's configuration state.
    /// Configuration and deployment failures are tracked on independent fields so a successful
    /// deploy never masks an unrelated configuration error (and vice versa) — the conflation of
    /// the two on a single <c>StatusMessage</c> field was the original UX bug this split fixes.
    /// Pending is treated like a fresh attempt and clears the previous error, matching the
    /// deployment-side behaviour.
    /// </summary>
    private static void ApplyConfigurationErrorTracking(RtAdapter adapter,
        RtConfigurationStateEnum configurationState, string? stateMessage)
    {
        if (configurationState == RtConfigurationStateEnum.Error)
        {
            adapter.LastConfigurationError = string.IsNullOrWhiteSpace(stateMessage)
                ? "(no message)"
                : stateMessage;
            adapter.LastConfigurationErrorTimestamp = DateTime.UtcNow;
        }
        else if (configurationState == RtConfigurationStateEnum.Configured ||
                 configurationState == RtConfigurationStateEnum.Pending)
        {
            adapter.LastConfigurationError = null;
            adapter.LastConfigurationErrorTimestamp = null;
        }
    }

    #endregion
}