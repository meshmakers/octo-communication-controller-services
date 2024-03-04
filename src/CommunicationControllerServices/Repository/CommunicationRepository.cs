using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.ConstructionKit.Generated.System.Communication.v1;
using Meshmakers.Octo.ConstructionKit.Models.System.ConstructionKit.Generated.System.v1;
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
    public async Task<IReadOnlyCollection<RtCommunicationAdapter>> GetAdaptersAsync(string tenantId, OctoObjectId poolRtId)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        var session = await tenantRepository.GetSessionAsync();
        try
        {
            session.StartTransaction();

            var resultSet = await tenantRepository.GetRtAssociationTargetsAsync<RtCommunicationPool, RtCommunicationAdapter>(session,
                new[] { poolRtId }, new CkId<CkAssociationRoleId>(SystemCkIds.ModelId, SystemCkIds.ParentChild),
                GraphDirections.Inbound, null, DataQueryOperation.Create());

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
    public async Task<IReadOnlyCollection<RtCommunicationAdapter>> GetAdaptersAsync(string tenantId)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        var session = await tenantRepository.GetSessionAsync();
        try
        {
            session.StartTransaction();

            var resultSet = await tenantRepository.GetRtEntitiesByTypeAsync<RtCommunicationAdapter>(session, DataQueryOperation.Create());

            await session.CommitTransactionAsync();

            return resultSet.Items.ToList();
        }
        catch (Exception e)
        {
            throw CommunicationRepositoryException.CommonFailedGettingAdapters(tenantId, e);
        }
    }

    /// <inheritdoc />
    public async Task<RtCommunicationAdapter> GetAdapterAsync(string tenantId, OctoObjectId adapterRtId)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        var session = await tenantRepository.GetSessionAsync();
        try
        {
            session.StartTransaction();

            var adapter = await tenantRepository.GetRtEntityByRtIdAsync<RtCommunicationAdapter>(session, adapterRtId);

            if (adapter == null)
            {
                throw CommunicationRepositoryException.AdapterNotFound(tenantId, adapterRtId);
            }

            await session.CommitTransactionAsync();

            return adapter;
        }
        catch (Exception e)
        {
            throw CommunicationRepositoryException.CommonFailedGettingAdapter(tenantId, adapterRtId, e);
        }
    }

    public async Task<IReadOnlyCollection<RtCommunicationPool>> GetPoolsAsync(string tenantId)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        var session = await tenantRepository.GetSessionAsync();
        try
        {
            session.StartTransaction();

            var dataQueryOperation = DataQueryOperation.Create();
            var poolResultSet = await tenantRepository.GetRtEntitiesByTypeAsync<RtCommunicationPool>(session, dataQueryOperation);

            await session.CommitTransactionAsync();

            return poolResultSet.Items.ToList();
        }
        catch (Exception e)
        {
            throw CommunicationRepositoryException.CommonFailedGettingPools(tenantId, e);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyCollection<RtCommunicationPool>> GetPoolByNameAsync(string tenantId, string poolName)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        var session = await tenantRepository.GetSessionAsync();
        try
        {
            session.StartTransaction();

            var dataQueryOperation = DataQueryOperation.Create()
                .FieldFilter(nameof(RtCommunicationPool.Name), FieldFilterOperator.Equals, poolName);

            var poolResultSet = await tenantRepository.GetRtEntitiesByTypeAsync<RtCommunicationPool>(session, dataQueryOperation);

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

            var rtCommunicationPool = new RtCommunicationPool
            {
                CommunicationState = RtCommunicationStateEnum.Offline,
                DeploymentState = RtDeploymentStateEnum.Created,
                ConfigurationState = RtConfigurationStateEnum.Disabled,
                Name = poolName
            };

            var entityUpdateInfoList = new List<EntityUpdateInfo<RtCommunicationPool>>
            {
                EntityUpdateInfo<RtCommunicationPool>.CreateInsert(rtCommunicationPool)
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
            throw CommunicationRepositoryException.CommonFailedCreatePool(tenantId, poolName, e);
        }
    }

    public async Task SetPoolDeploymentStateAsync(string tenantId, OctoObjectId poolRtId, RtDeploymentStateEnum deploymentState)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        var session = await tenantRepository.GetSessionAsync();
        try
        {
            session.StartTransaction();

            var rtCommunicationPool = new RtCommunicationPool
            {
                RtId = poolRtId,
                DeploymentState = deploymentState
            };

            var entityUpdateInfoList = new List<EntityUpdateInfo<RtCommunicationPool>>
            {
                EntityUpdateInfo<RtCommunicationPool>.CreateUpdate(rtCommunicationPool.ToRtEntityId(), rtCommunicationPool)
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
            throw CommunicationRepositoryException.CommonFailedSetPoolDeploymentState(tenantId, poolRtId, deploymentState, e);
        }
    }

    public async Task SetPoolCommunicationStateAsync(string tenantId, OctoObjectId poolRtId, RtCommunicationStateEnum communicationState)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        var session = await tenantRepository.GetSessionAsync();
        try
        {
            session.StartTransaction();

            var rtCommunicationPool = new RtCommunicationPool
            {
                RtId = poolRtId,
                CommunicationState = communicationState
            };

            var entityUpdateInfoList = new List<EntityUpdateInfo<RtCommunicationPool>>
            {
                EntityUpdateInfo<RtCommunicationPool>.CreateUpdate(rtCommunicationPool.ToRtEntityId(), rtCommunicationPool)
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
            throw CommunicationRepositoryException.CommonFailedSetPoolCommunicationState(tenantId, poolRtId, communicationState, e);
        }
    }

    public async Task SetAdapterDeploymentStateAsync(string tenantId, OctoObjectId adapterRtId, RtDeploymentStateEnum deploymentState)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        var session = await tenantRepository.GetSessionAsync();
        try
        {
            session.StartTransaction();

            var rtAdapter = new RtCommunicationAdapter
            {
                RtId = adapterRtId,
                DeploymentState = deploymentState
            };

            var entityUpdateInfoList = new List<EntityUpdateInfo<RtCommunicationAdapter>>
            {
                EntityUpdateInfo<RtCommunicationAdapter>.CreateUpdate(rtAdapter.ToRtEntityId(), rtAdapter)
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
            throw CommunicationRepositoryException.CommonFailedSetAdapterDeploymentState(tenantId, adapterRtId, deploymentState, e);
        }
    }

    public async Task SetAdapterCommunicationStateAsync(string tenantId, OctoObjectId adapterRtId,
        RtCommunicationStateEnum communicationState)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        var session = await tenantRepository.GetSessionAsync();
        try
        {
            session.StartTransaction();

            var rtAdapter = new RtCommunicationAdapter
            {
                RtId = adapterRtId,
                CommunicationState = communicationState,
                LastSeen = DateTime.UtcNow
            };

            var entityUpdateInfoList = new List<EntityUpdateInfo<RtCommunicationAdapter>>
            {
                EntityUpdateInfo<RtCommunicationAdapter>.CreateUpdate(rtAdapter.ToRtEntityId(), rtAdapter)
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
            throw CommunicationRepositoryException.CommonFailedSetAdapterCommunicationState(tenantId, adapterRtId, communicationState, e);
        }
    }

    /// <inheritdoc />
    public async Task<RtCommunicationPool> GetPoolOfAdapterAsync(string tenantId, OctoObjectId adapterRtId)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        var session = await tenantRepository.GetSessionAsync();
        try
        {
            session.StartTransaction();

            var poolResultSet = await tenantRepository.GetRtAssociationTargetsAsync<RtCommunicationAdapter, RtCommunicationPool>(session,
                new[] { adapterRtId }, new CkId<CkAssociationRoleId>(SystemCkIds.ModelId, SystemCkIds.ParentChild),
                GraphDirections.Inbound, null, DataQueryOperation.Create());

            await session.CommitTransactionAsync();

            if (poolResultSet.Any())
            {
                var pool = poolResultSet.First().Value.Items.FirstOrDefault();
                if (pool != null)
                {
                    return pool;
                }

                throw CommunicationRepositoryException.AdapterNotAssociatedToPool(tenantId, adapterRtId);
            }

            throw CommunicationRepositoryException.AdapterNotFound(tenantId, adapterRtId);
        }
        catch (Exception e)
        {
            throw CommunicationRepositoryException.CommonGettingPoolOfAdapter(tenantId, adapterRtId, e);
        }
    }

    /// <inheritdoc />
    public async Task<RtCommunicationAdapter> GetAdapterByDataPipelineAsync(string tenantId, OctoObjectId dataPipelineRtId)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        var session = await tenantRepository.GetSessionAsync();
        try
        {
            session.StartTransaction();

            var poolResultSet = await tenantRepository.GetIndirectRtAssociationTargetsAsync<RtDataPipeline, RtCommunicationAdapter>(session,
                dataPipelineRtId, new CkId<CkAssociationRoleId>(SystemCkIds.ModelId, SystemCkIds.ParentChild),
                GraphDirections.Inbound);

            await session.CommitTransactionAsync();

            if (poolResultSet != null)
            {
                if (poolResultSet.Items.Any())
                {
                    return poolResultSet.Items.First();
                }

                throw CommunicationRepositoryException.DataPipelineNotAssociatedToAdapter(tenantId, dataPipelineRtId);
            }

            throw CommunicationRepositoryException.DataPipelineNotFound(tenantId, dataPipelineRtId);
        }
        catch (Exception e)
        {
            throw CommunicationRepositoryException.CommonGettingAdapterByDataPipeline(tenantId, dataPipelineRtId, e);
        }
    }

    public async Task<bool> IsTenantExistingAsync(string tenantId)
    {
        var systemSession = await _systemContext.GetSystemSessionAsync();

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

    public async Task<IReadOnlyCollection<RtDataPipeline>> GetDataPipelinesAsync(string tenantId, OctoObjectId adapterRtId)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        var session = await tenantRepository.GetSessionAsync();
        try
        {
            session.StartTransaction();

            var multipleOriginResultSet = await tenantRepository.GetRtAssociationTargetsAsync<RtCommunicationAdapter, RtDataPipeline>(
                session,
                new[] { adapterRtId }, new CkId<CkAssociationRoleId>(SystemCkIds.ModelId, SystemCkIds.ParentChild),
                GraphDirections.Inbound, null, DataQueryOperation.Create());

            await session.CommitTransactionAsync();

            if (multipleOriginResultSet.Any())
            {
                return multipleOriginResultSet.First().Value.Items.ToList();
            }

            throw CommunicationRepositoryException.AdapterNotFound(tenantId, adapterRtId);
        }
        catch (Exception e)
        {
            throw CommunicationRepositoryException.CommonFailedGettingAdapter(tenantId, adapterRtId, e);
        }
    }
}