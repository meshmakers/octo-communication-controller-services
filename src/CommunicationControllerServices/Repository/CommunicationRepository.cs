using Meshmakers.Octo.Backend.CommunicationControllerServices.Models;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v3;
using Microsoft.Extensions.Logging;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;

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
    public async Task<IReadOnlyCollection<RtAdapter>> GetAdaptersAsync(string tenantId, OctoObjectId poolRtId)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        using var session = await tenantRepository.GetSessionAsync();
        try
        {
            var resultSet = await tenantRepository.GetRtAssociationTargetsAsync<RtPool, RtAdapter>(session,
                [poolRtId] , SystemCommunicationCkIds.RtCkManagesRoleId,
                GraphDirections.Inbound, null, RtEntityQueryOptions.Create());

            if (!resultSet.Any())
            {
                throw CommunicationRepositoryException.PoolNotFound(tenantId, poolRtId);
            }

            return resultSet.First().Value.Items.ToList();
        }
        catch (Exception e)
        {
            throw CommunicationRepositoryException.CommonFailedGettingAdapters(tenantId, poolRtId, e);
        }
    }


    /// <inheritdoc />
    public async Task<IReadOnlyCollection<RtDeployableWorkload>> GetWorkloadsForPoolAsync(string tenantId,
        OctoObjectId poolRtId)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        using var session = await tenantRepository.GetSessionAsync();
        try
        {
            // RtDeployableWorkload is abstract — the runtime engine returns the
            // concrete RtAdapter / RtApplication instances polymorphically.
            var resultSet = await tenantRepository.GetRtAssociationTargetsAsync<RtPool, RtDeployableWorkload>(session,
                [poolRtId], SystemCommunicationCkIds.RtCkManagesRoleId,
                GraphDirections.Inbound, null, RtEntityQueryOptions.Create());

            if (!resultSet.Any())
            {
                return Array.Empty<RtDeployableWorkload>();
            }

            return resultSet.First().Value.Items.ToList();
        }
        catch (Exception e)
        {
            throw CommunicationRepositoryException.CommonFailedGettingAdapters(tenantId, poolRtId, e);
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
    public async Task<RtPool?> GetPoolForWorkloadAsync(string tenantId, OctoObjectId workloadRtId)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        using var session = await tenantRepository.GetSessionAsync();
        try
        {
            // Workload's outbound Manages association points at the parent pool.
            var resultSet = await tenantRepository
                .GetRtAssociationTargetsAsync<RtDeployableWorkload, RtPool>(session,
                    [workloadRtId], SystemCommunicationCkIds.RtCkManagesRoleId,
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

    public async Task<IReadOnlyCollection<RtPool>> GetPoolsAsync(string tenantId)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        using var session = await tenantRepository.GetSessionAsync();
        try
        {
            var dataQueryOperation = RtEntityQueryOptions.Create();
            var poolResultSet = await tenantRepository.GetRtEntitiesByTypeAsync<RtPool>(session, dataQueryOperation);

            return poolResultSet.Items.ToList();
        }
        catch (Exception e)
        {
            throw CommunicationRepositoryException.CommonFailedGettingPools(tenantId, e);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyCollection<RtPool>> GetPoolByNameAsync(string tenantId, string poolName)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        using var session = await tenantRepository.GetSessionAsync();
        try
        {
            var dataQueryOperation = RtEntityQueryOptions.Create()
                .FieldFilter(nameof(RtPool.Name), FieldFilterOperator.Equals, poolName);

            var poolResultSet = await tenantRepository.GetRtEntitiesByTypeAsync<RtPool>(session, dataQueryOperation);

            return poolResultSet.Items.ToList();
        }
        catch (Exception e)
        {
            throw CommunicationRepositoryException.CommonFailedGettingPoolByName(tenantId, poolName, e);
        }
    }

    /// <inheritdoc />
    public async Task CreatePoolAsync(string tenantId, string poolName)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        using var session = await tenantRepository.GetSessionAsync();
        try
        {
            session.StartTransaction();

            var rtPool = new RtPool
            {
                CommunicationState = RtCommunicationStateEnum.Offline,
                DeploymentState = RtDeploymentStateEnum.Undeployed,
                ConfigurationState = RtConfigurationStateEnum.Unconfigured,
                Name = poolName
            };

            var entityUpdateInfoList = new List<EntityUpdateInfo<RtPool>>
            {
                EntityUpdateInfo<RtPool>.CreateInsert(rtPool)
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
            throw CommunicationRepositoryException.CommonFailedCreatePool(tenantId, poolName, e);
        }
    }

    public async Task SetPoolDeploymentStateAsync(string tenantId, OctoObjectId poolRtId,
        RtDeploymentStateEnum deploymentState)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        using var session = await tenantRepository.GetSessionAsync();
        try
        {
            session.StartTransaction();

            var rtPool = new RtPool
            {
                RtId = poolRtId,
                DeploymentState = deploymentState
            };

            var entityUpdateInfoList = new List<EntityUpdateInfo<RtPool>>
            {
                EntityUpdateInfo<RtPool>.CreateUpdate(rtPool.ToRtEntityId(), rtPool)
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
            throw CommunicationRepositoryException.CommonFailedSetPoolDeploymentState(tenantId, poolRtId,
                deploymentState, e);
        }
    }

    public async Task SetPoolCommunicationStateAsync(string tenantId, OctoObjectId poolRtId,
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
            var rtPool = new RtPool
            {
                RtId = poolRtId,
                CommunicationState = communicationState,
                CommunicationStateTimestamp = newTimestamp
            };

            var guard = new AttributeNewerThanGuard("attributes.communicationStateTimestamp", newTimestamp);
            var entityUpdateInfoList = new List<EntityUpdateInfo<RtPool>>
            {
                EntityUpdateInfo<RtPool>.CreateConditionalUpdate(rtPool.ToRtEntityId(), rtPool, guard)
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
            throw CommunicationRepositoryException.CommonFailedSetPoolCommunicationState(tenantId, poolRtId,
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
    public async Task<RtPool> GetPoolOfAdapterAsync(string tenantId, RtEntityId adapterRtEntityId)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        using var session = await tenantRepository.GetSessionAsync();
        try
        {
            var poolResultSet = await tenantRepository.GetRtAssociationTargetsAsync<RtAdapter, RtPool>(session,
                [adapterRtEntityId.RtId],
                SystemCommunicationCkIds.RtCkManagesRoleId,
                GraphDirections.Inbound, null, RtEntityQueryOptions.Create());

            if (poolResultSet.Any())
            {
                var pool = poolResultSet.First().Value.Items.FirstOrDefault();
                if (pool != null)
                {
                    return pool;
                }

                throw CommunicationRepositoryException.AdapterNotAssociatedToPool(tenantId, adapterRtEntityId);
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

    public async Task SetPipelineDefinitionAsync(string tenantId, RtEntityId pipelineRtEntityId,
        string pipelineDefinition)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        using var session = await tenantRepository.GetSessionAsync();
        try
        {
            session.StartTransaction();

            var pipeline = new RtPipeline
            {
                PipelineDefinition = pipelineDefinition
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
            var queryOptions = RtEntityQueryOptions.Create()
                .FieldFilter(nameof(RtPipelineExecution.StartedAt), FieldFilterOperator.LessThan, olderThan);

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
                await tenantRepository.ApplyChangesAsync(session, entityUpdateInfoList, operationResult);
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