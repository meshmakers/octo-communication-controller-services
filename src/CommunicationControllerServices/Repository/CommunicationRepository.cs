using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v2;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;

/// <summary>
/// Repository for pool related operations
/// </summary>
internal class CommunicationRepository : ICommunicationRepository
{
    private readonly ISystemContext _systemContext;

    /// <summary>
    /// Constructor
    /// </summary>
    /// <param name="systemContext">The root object of the persistence layer</param>
    public CommunicationRepository(ISystemContext systemContext)
    {
        _systemContext = systemContext;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyCollection<RtAdapter>> GetAdaptersAsync(string tenantId, OctoObjectId poolRtId)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        var session = await tenantRepository.GetSessionAsync();
        try
        {
            session.StartTransaction();

            var resultSet = await tenantRepository.GetRtAssociationTargetsAsync<RtPool, RtAdapter>(session,
                [poolRtId] , SystemCommunicationCkIds.RtCkManagesRoleId,
                GraphDirections.Inbound, null, RtEntityQueryOptions.Create());

            if (!resultSet.Any())
            {
                throw CommunicationRepositoryException.PoolNotFound(tenantId, poolRtId);
            }

            var list = resultSet.First().Value.Items.ToList();

            await session.CommitTransactionAsync();

            return list;
        }
        catch (Exception e)
        {
            throw CommunicationRepositoryException.CommonFailedGettingAdapters(tenantId, poolRtId, e);
        }
    }


    /// <inheritdoc />
    public async Task<IReadOnlyCollection<RtAdapter>> GetAdaptersAsync(string tenantId)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        var session = await tenantRepository.GetSessionAsync();
        try
        {
            session.StartTransaction();

            var resultSet =
                await tenantRepository.GetRtEntitiesByTypeAsync<RtAdapter>(session, RtEntityQueryOptions.Create());

            await session.CommitTransactionAsync();

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

        var session = await tenantRepository.GetSessionAsync();
        try
        {
            session.StartTransaction();

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

            await session.CommitTransactionAsync();

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

        var session = await tenantRepository.GetSessionAsync();
        try
        {
            session.StartTransaction();

            var multipleOriginResultSet =
                await tenantRepository.GetRtAssociationTargetsAsync<RtPipeline, RtAdapter>(
                    session,
                    [pipelineRtEntityId.RtId], SystemCommunicationCkIds.RtCkExecutesRoleId,
                    GraphDirections.Outbound, null, RtEntityQueryOptions.Create());

            await session.CommitTransactionAsync();

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

    public async Task<IReadOnlyCollection<RtPool>> GetPoolsAsync(string tenantId)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        var session = await tenantRepository.GetSessionAsync();
        try
        {
            session.StartTransaction();

            var dataQueryOperation = RtEntityQueryOptions.Create();
            var poolResultSet = await tenantRepository.GetRtEntitiesByTypeAsync<RtPool>(session, dataQueryOperation);

            await session.CommitTransactionAsync();

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

        var session = await tenantRepository.GetSessionAsync();
        try
        {
            session.StartTransaction();

            var dataQueryOperation = RtEntityQueryOptions.Create()
                .FieldFilter(nameof(RtPool.Name), FieldFilterOperator.Equals, poolName);

            var poolResultSet = await tenantRepository.GetRtEntitiesByTypeAsync<RtPool>(session, dataQueryOperation);

            await session.CommitTransactionAsync();

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

        var session = await tenantRepository.GetSessionAsync();
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

        var session = await tenantRepository.GetSessionAsync();
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

        var session = await tenantRepository.GetSessionAsync();
        try
        {
            session.StartTransaction();

            var rtPool = new RtPool
            {
                RtId = poolRtId,
                CommunicationState = communicationState,
                CommunicationStateTimestamp = DateTime.UtcNow
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
            throw CommunicationRepositoryException.CommonFailedSetPoolCommunicationState(tenantId, poolRtId,
                communicationState, e);
        }
    }

    public async Task SetAdapterDeploymentStateAsync(string tenantId, RtEntityId adapterRtEntityId,
        RtDeploymentStateEnum deploymentState)
    {
        await SetAdapterDeploymentStateAsync(tenantId, [adapterRtEntityId], deploymentState);
    }

    public async Task SetAdapterDeploymentStateAsync(string tenantId, ICollection<RtEntityId> adapterRtEntityIds,
        RtDeploymentStateEnum deploymentState)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        var session = await tenantRepository.GetSessionAsync();
        try
        {
            session.StartTransaction();

            var rtAdapter = new RtAdapter
            {
                DeploymentState = deploymentState
            };

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

    public async Task SetAdapterCommunicationStateAsync(string tenantId, RtEntityId adapterRtEntityId,
        RtCommunicationStateEnum communicationState)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        var session = await tenantRepository.GetSessionAsync();
        try
        {
            session.StartTransaction();

            var rtAdapter = new RtAdapter
            {
                CommunicationState = communicationState,
                CommunicationStateTimestamp = DateTime.UtcNow
            };

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
            throw CommunicationRepositoryException.CommonFailedSetAdapterCommunicationState(tenantId, adapterRtEntityId,
                communicationState, e);
        }
    }

    /// <inheritdoc />
    public async Task<RtPool> GetPoolOfAdapterAsync(string tenantId, RtEntityId adapterRtEntityId)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        var session = await tenantRepository.GetSessionAsync();
        try
        {
            session.StartTransaction();

            var poolResultSet = await tenantRepository.GetRtAssociationTargetsAsync<RtAdapter, RtPool>(session,
                [adapterRtEntityId.RtId],
                SystemCommunicationCkIds.RtCkManagesRoleId,
                GraphDirections.Inbound, null, RtEntityQueryOptions.Create());

            await session.CommitTransactionAsync();

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
            systemSession.StartTransaction();

            var isTenantExisting = await _systemContext.IsChildTenantExistingAsync(systemSession, tenantId);

            await systemSession.CommitTransactionAsync();

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

        var session = await tenantRepository.GetSessionAsync();
        try
        {
            session.StartTransaction();

            var multipleOriginResultSet = await tenantRepository.GetRtAssociationTargetsAsync<RtAdapter, RtPipeline>(
                session,
                [adapterRtEntityId.RtId],
                SystemCommunicationCkIds.RtCkExecutesRoleId,
                GraphDirections.Inbound, null, RtEntityQueryOptions.Create());

            await session.CommitTransactionAsync();

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

    public async Task<IReadOnlyCollection<RtPipeline>> GetPipelinesAsync(string tenantId, OctoObjectId dataPipelineRtId)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        var session = await tenantRepository.GetSessionAsync();
        try
        {
            session.StartTransaction();

            var originResultSet = await tenantRepository.GetRtAssociationTargetsAsync<RtDataPipeline, RtPipeline>(session,
                [dataPipelineRtId],
                SystemCkIds.RtCkParentChildRoleId,
                GraphDirections.Inbound, null, RtEntityQueryOptions.Create());

            await session.CommitTransactionAsync();

            if (originResultSet.Any())
            {
                var pool = originResultSet.First().Value.Items;
                return pool.ToList();
            }

            throw CommunicationRepositoryException.DataPipelineNotFound(tenantId, dataPipelineRtId);
        }
        catch (CommunicationRepositoryException)
        {
            throw;
        }
        catch (Exception e)
        {
            throw CommunicationRepositoryException.CommonFailedGettingByDataPipeline(tenantId, dataPipelineRtId, e);
        }
    }

    public async Task<RtPipeline?> GetPipelineAsync(string tenantId, RtEntityId pipelineRtEntityId)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        var session = await tenantRepository.GetSessionAsync();
        try
        {
            session.StartTransaction();

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

    public async Task<RtDataPipeline?> GetDataPipelineByPipelineAsync(string tenantId, OctoObjectId pipelineRtId)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        var session = await tenantRepository.GetSessionAsync();
        try
        {
            session.StartTransaction();

            var multipleOriginResultSet =
                await tenantRepository.GetRtAssociationTargetsAsync<RtPipeline, RtDataPipeline>(
                    session,
                    [pipelineRtId],
                    SystemCkIds.RtCkParentChildRoleId,
                    GraphDirections.Outbound, null, RtEntityQueryOptions.Create());

            await session.CommitTransactionAsync();

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

        var session = await tenantRepository.GetSessionAsync();
        try
        {
            session.StartTransaction();
            
            var multipleOriginResultSet =
                await tenantRepository.GetRtAssociationTargetsAsync<RtPipeline, RtConfiguration>(
                    session,
                    [pipelineRtId],
                    SystemCommunicationCkIds.RtCkUsesRoleId,
                    GraphDirections.Outbound, null, RtEntityQueryOptions.Create());

            await session.CommitTransactionAsync();

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

    public async Task<IReadOnlyCollection<RtDataPipelineTrigger>> GetTriggersAsync(string tenantId)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        var session = await tenantRepository.GetSessionAsync();
        try
        {
            session.StartTransaction();

            var queryOptions = RtEntityQueryOptions.Create();
            var r = await tenantRepository.GetRtEntitiesByTypeAsync<RtDataPipelineTrigger>(session, queryOptions);

            await session.CommitTransactionAsync();
            return r.Items.ToList();
        }
        catch (Exception e)
        {
            throw CommunicationRepositoryException.CommonFailedGettingTriggers(tenantId, e);
        }
    }

    public async Task<IDictionary<RtDataPipelineTrigger, IList<RtMeshPipeline>>> GetTriggersAndPipelinesAsync(
        string tenantId)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        var session = await tenantRepository.GetSessionAsync();
        try
        {
            session.StartTransaction();

            var queryOptions = RtEntityQueryOptions.Create()
                .FieldEquals(nameof(RtDataPipelineTrigger.Enabled), true);

            var r = await tenantRepository.GetRtEntitiesByTypeAsync<RtDataPipelineTrigger>(session, queryOptions);

            queryOptions = RtEntityQueryOptions.Create();
            var a = await tenantRepository.GetRtAssociationTargetsAsync<RtDataPipelineTrigger, RtMeshPipeline>(session,
                r.Items.Select(x => x.RtId).ToList(),
                SystemCommunicationCkIds.RtCkTriggersRoleId, GraphDirections.Inbound, null, queryOptions);

            Dictionary<RtDataPipelineTrigger, IList<RtMeshPipeline>> list = new();
            foreach (var pipelineTrigger in r.Items)
            {
                if (a.TryGetValue(pipelineTrigger.ToRtEntityId(), out var resultSet))
                {
                    list.Add(pipelineTrigger, resultSet.Items.ToList());
                }
            }

            await session.CommitTransactionAsync();
            return list;
        }
        catch (Exception e)
        {
            throw CommunicationRepositoryException.CommonFailedGettingTriggers(tenantId, e);
        }
    }

    public async Task SetDataPipelineTriggerDeploymentStateAsync(string tenantId, OctoObjectId triggerRtId,
        RtDeploymentStateEnum deploymentState)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        var session = await tenantRepository.GetSessionAsync();
        try
        {
            session.StartTransaction();

            var pipelineTrigger = new RtDataPipelineTrigger
            {
                DeploymentState = deploymentState
            };

            var entityUpdateInfoList = new List<EntityUpdateInfo<RtDataPipelineTrigger>>
            {
                EntityUpdateInfo<RtDataPipelineTrigger>.CreateUpdate(
                    new RtEntityId(SystemCommunicationCkIds.RtCkDataPipelineTriggerTypeId,
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

        var session = await tenantRepository.GetSessionAsync();
        try
        {
            session.StartTransaction();

            var pipeline = new RtPipeline
            {
                DeploymentState = deploymentState,
                StatusMessage = stateMessage
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
            throw CommunicationRepositoryException.CommonFailedSetPipelineDeploymentState(tenantId, pipelineRtEntityId,
                deploymentState, e);
        }
    }

    public async Task SetAdapterConfigurationStateAsync(string tenantId, RtEntityId adapterRtEntityId,
        RtConfigurationStateEnum configurationState, string? stateMessage)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        var session = await tenantRepository.GetSessionAsync();
        try
        {
            session.StartTransaction();

            var rtAdapter = new RtAdapter
            {
                ConfigurationState = configurationState,
                StatusMessage = stateMessage
            };

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

        var session = await tenantRepository.GetSessionAsync();
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
        RtPipelineExecutionStatusEnum status, DateTime? completedAt, int? durationMs, string? errorMessage)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        var session = await tenantRepository.GetSessionAsync();
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
                ErrorMessage = errorMessage
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

    public async Task<RtPipelineExecution?> GetPipelineExecutionAsync(string tenantId, string executionId)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        var session = await tenantRepository.GetSessionAsync();
        try
        {
            session.StartTransaction();

            var queryOptions = RtEntityQueryOptions.Create()
                .FieldFilter(nameof(RtPipelineExecution.ExecutionId), FieldFilterOperator.Equals, executionId);

            var resultSet = await tenantRepository.GetRtEntitiesByTypeAsync<RtPipelineExecution>(session, queryOptions);

            await session.CommitTransactionAsync();

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

        var session = await tenantRepository.GetSessionAsync();
        try
        {
            session.StartTransaction();

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
                queryOptions);

            await session.CommitTransactionAsync();

            if (resultSet.Any())
            {
                var items = resultSet.First().Value.Items.ToList();
                // Apply limit in memory if specified
                return limit.HasValue ? items.Take(limit.Value).ToList() : items;
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

        var session = await tenantRepository.GetSessionAsync();
        try
        {
            var queryOptions = RtEntityQueryOptions.Create()
                .FieldFilter(nameof(RtPipelineExecution.Status), FieldFilterOperator.Equals, (int)RtPipelineExecutionStatusEnum.Running);

            var resultSet = await tenantRepository.GetRtAssociationTargetsAsync<RtAdapter, RtPipelineExecution>(
                session,
                [adapterRtEntityId.RtId],
                SystemCommunicationCkIds.RtCkExecutingAdapterRoleId,
                GraphDirections.Inbound,
                null,
                queryOptions);

            if (resultSet.Any())
            {
                return resultSet.First().Value.Items.ToList();
            }

            return [];
        }
        catch (Exception e)
        {
            throw CommunicationRepositoryException.CommonFailedGetRunningExecutions(tenantId, adapterRtEntityId, e);
        }
    }

    public async Task<IReadOnlyList<string>> GetInterruptedExecutionIdsAsync(string tenantId, RtEntityId adapterRtEntityId)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        var session = await tenantRepository.GetSessionAsync();
        try
        {
            var queryOptions = RtEntityQueryOptions.Create()
                .FieldFilter(nameof(RtPipelineExecution.Status), FieldFilterOperator.Equals, (int)RtPipelineExecutionStatusEnum.Interrupted);

            var resultSet = await tenantRepository.GetRtAssociationTargetsAsync<RtAdapter, RtPipelineExecution>(
                session,
                [adapterRtEntityId.RtId],
                SystemCommunicationCkIds.RtCkExecutingAdapterRoleId,
                GraphDirections.Inbound,
                null,
                queryOptions);

            if (resultSet.Any())
            {
                return resultSet.First().Value.Items
                    .Where(e => e.ExecutionId != null)
                    .Select(e => e.ExecutionId!)
                    .ToList();
            }

            return [];
        }
        catch (Exception e)
        {
            throw CommunicationRepositoryException.CommonFailedGetInterruptedExecutions(tenantId, adapterRtEntityId, e);
        }
    }

    public async Task<int> DeleteOldExecutionsAsync(string tenantId, DateTime olderThan)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);
        const int batchSize = 500;

        try
        {
            // Query all old executions once
            var querySession = await tenantRepository.GetSessionAsync();
            querySession.StartTransaction();

            var queryOptions = RtEntityQueryOptions.Create()
                .FieldFilter(nameof(RtPipelineExecution.StartedAt), FieldFilterOperator.LessThan, olderThan);

            var resultSet = await tenantRepository.GetRtEntitiesByTypeAsync<RtPipelineExecution>(querySession, queryOptions);
            var allExecutions = resultSet.Items.ToList();

            await querySession.CommitTransactionAsync();

            if (!allExecutions.Any())
            {
                return 0;
            }

            // Delete in batches to avoid oversized transactions
            var totalDeleted = 0;
            foreach (var batch in allExecutions.Chunk(batchSize))
            {
                var session = await tenantRepository.GetSessionAsync();
                session.StartTransaction();

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
                totalDeleted += batch.Length;
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

        var session = await tenantRepository.GetSessionAsync();
        try
        {
            session.StartTransaction();

            var queryOptions = RtEntityQueryOptions.Create()
                .FieldFilter(nameof(RtPipelineExecution.Status), FieldFilterOperator.Equals, (int)RtPipelineExecutionStatusEnum.Running)
                .FieldFilter(nameof(RtPipelineExecution.StartedAt), FieldFilterOperator.LessThan, olderThan);

            var resultSet = await tenantRepository.GetRtEntitiesByTypeAsync<RtPipelineExecution>(session, queryOptions);
            var staleExecutions = resultSet.Items.ToList();

            if (!staleExecutions.Any())
            {
                await session.CommitTransactionAsync();
                return 0;
            }

            var entityUpdateInfoList = staleExecutions
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
            return staleExecutions.Count;
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

        var session = await tenantRepository.GetSessionAsync();
        try
        {
            session.StartTransaction();

            var queryOptions = RtEntityQueryOptions.Create();

            var resultSet = await tenantRepository.GetRtAssociationTargetsAsync<RtPipeline, RtPipelineStatistics>(
                session,
                [pipelineRtEntityId.RtId],
                SystemCommunicationCkIds.RtCkStatisticsForPipelineRoleId,
                GraphDirections.Inbound,
                null,
                queryOptions);

            await session.CommitTransactionAsync();

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

        var session = await tenantRepository.GetSessionAsync();
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

        var session = await tenantRepository.GetSessionAsync();
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

        var session = await tenantRepository.GetSessionAsync();
        try
        {
            session.StartTransaction();

            var executionList = executions.ToList();
            var entityUpdateInfoList = executionList
                .Select(EntityUpdateInfo<RtPipelineExecution>.CreateInsert)
                .ToList();

            OperationResult operationResult = new();
            await tenantRepository.ApplyChangesAsync(session, entityUpdateInfoList, operationResult);
            if (operationResult.HasErrors || operationResult.HasFatalErrors)
            {
                throw CommunicationRepositoryException.CommonOperationFailed(operationResult);
            }

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
            throw CommunicationRepositoryException.CommonFailedBulkInsertExecutions(tenantId, e);
        }
    }

    public async Task<ISet<string>> GetExistingExecutionIdsAsync(string tenantId, IEnumerable<string> executionIds)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        var session = await tenantRepository.GetSessionAsync();
        try
        {
            session.StartTransaction();

            var idList = executionIds.ToList();
            var queryOptions = RtEntityQueryOptions.Create()
                .FieldFilter(nameof(RtPipelineExecution.ExecutionId), FieldFilterOperator.In, idList);

            var resultSet = await tenantRepository.GetRtEntitiesByTypeAsync<RtPipelineExecution>(session, queryOptions);

            await session.CommitTransactionAsync();

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

        var session = await tenantRepository.GetSessionAsync();
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

        var session = await tenantRepository.GetSessionAsync();
        try
        {
            session.StartTransaction();

            var queryOptions = RtEntityQueryOptions.Create();
            var resultSet = await tenantRepository.GetRtEntitiesByTypeAsync<RtPipeline>(session, queryOptions);

            await session.CommitTransactionAsync();

            return resultSet.Items.ToList();
        }
        catch (Exception e)
        {
            throw CommunicationRepositoryException.CommonFailedGetAllPipelines(tenantId, e);
        }
    }

    #endregion
}