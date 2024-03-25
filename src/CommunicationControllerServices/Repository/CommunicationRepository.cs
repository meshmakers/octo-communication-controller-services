using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v1;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v1;
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
    public async Task<IReadOnlyCollection<RtAdapter>> GetAdaptersAsync(string tenantId)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        var session = await tenantRepository.GetSessionAsync();
        try
        {
            session.StartTransaction();

            var resultSet = await tenantRepository.GetRtEntitiesByTypeAsync<RtAdapter>(session, DataQueryOperation.Create());

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

            await session.CommitTransactionAsync();

            return adapter;
        }
        catch (Exception e)
        {
            throw CommunicationRepositoryException.CommonFailedGettingAdapter(tenantId, adapterRtEntityId, e);
        }
    }

    public async Task<IReadOnlyCollection<RtPool>> GetPoolsAsync(string tenantId)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        var session = await tenantRepository.GetSessionAsync();
        try
        {
            session.StartTransaction();

            var dataQueryOperation = DataQueryOperation.Create();
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

            var dataQueryOperation = DataQueryOperation.Create()
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
                DeploymentState = RtDeploymentStateEnum.Unknown,
                ConfigurationState = RtConfigurationStateEnum.Disabled,
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

            var rtPool = new RtPool
            {
                RtId = poolRtId,
                CommunicationState = communicationState
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
        catch (Exception e)
        {
            throw CommunicationRepositoryException.CommonFailedSetPoolCommunicationState(tenantId, poolRtId, communicationState, e);
        }
    }

    public async Task SetAdapterDeploymentStateAsync(string tenantId, RtEntityId adapterRtEntityId, RtDeploymentStateEnum deploymentState)
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
        catch (Exception e)
        {
            throw CommunicationRepositoryException.CommonFailedSetAdapterDeploymentState(tenantId, adapterRtEntityId, deploymentState, e);
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
                LastSeen = DateTime.UtcNow
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
        catch (Exception e)
        {
            throw CommunicationRepositoryException.CommonFailedSetAdapterCommunicationState(tenantId, adapterRtEntityId, communicationState, e);
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
                new[] { adapterRtEntityId.RtId }, new CkId<CkAssociationRoleId>(SystemCkIds.ModelId, SystemCkIds.ParentChild),
                GraphDirections.Inbound, null, DataQueryOperation.Create());

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
        catch (Exception e)
        {
            throw CommunicationRepositoryException.CommonGettingPoolOfAdapter(tenantId, adapterRtEntityId, e);
        }
    }

    /// <inheritdoc />
    public async Task<RtAdapter> GetAdapterByDataPipelineAsync(string tenantId, OctoObjectId dataPipelineRtId)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        var session = await tenantRepository.GetSessionAsync();
        try
        {
            session.StartTransaction();

            var poolResultSet = await tenantRepository.GetIndirectRtAssociationTargetsAsync<RtDataPipeline, RtAdapter>(session,
                dataPipelineRtId, new CkId<CkAssociationRoleId>(SystemCkIds.ModelId, SystemCkIds.ParentChild),
                GraphDirections.Inbound);

            await session.CommitTransactionAsync();

            if (poolResultSet != null)
            {
                if (poolResultSet.Items.Any())
                {
                    return poolResultSet.Items.First();
                }

                throw CommunicationRepositoryException.PipelineNotAssociatedToAdapter(tenantId, dataPipelineRtId);
            }

            throw CommunicationRepositoryException.PipelineNotFound(tenantId, dataPipelineRtId);
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

    public async Task<IReadOnlyCollection<RtPipeline>> GetPipelinesAsync(string tenantId, RtEntityId adapterRtEntityId)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        var session = await tenantRepository.GetSessionAsync();
        try
        {
            session.StartTransaction();

            var multipleOriginResultSet = await tenantRepository.GetRtAssociationTargetsAsync<RtAdapter, RtPipeline>(
                session,
                new[] { adapterRtEntityId.RtId }, new CkId<CkAssociationRoleId>(SystemCommunicationCkIds.ModelId, SystemCommunicationCkIds.Executes),
                GraphDirections.Inbound, null, DataQueryOperation.Create());

            await session.CommitTransactionAsync();

            if (multipleOriginResultSet.Any())
            {
                return multipleOriginResultSet.First().Value.Items.ToList();
            }

            throw CommunicationRepositoryException.AdapterNotFound(tenantId, adapterRtEntityId);
        }
        catch (Exception e)
        {
            throw CommunicationRepositoryException.CommonFailedGettingAdapter(tenantId, adapterRtEntityId, e);
        }
    }

    public async Task<RtPipeline?> GetPipelineAsync(string tenantId, RtEntityId pipelineRtEntityId)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        var session = await tenantRepository.GetSessionAsync();
        try
        {
            session.StartTransaction();

            var rtPipeline = await tenantRepository.GetRtEntityByRtIdAsync<RtPipeline>(session, pipelineRtEntityId.RtId);
            return rtPipeline;
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

            var multipleOriginResultSet = await tenantRepository.GetRtAssociationTargetsAsync<RtPipeline, RtDataPipeline>(
                session,
                new[] { pipelineRtId }, new CkId<CkAssociationRoleId>(SystemCkIds.ModelId, SystemCkIds.ParentChild),
                GraphDirections.Outbound, null, DataQueryOperation.Create());

            await session.CommitTransactionAsync();

            if (multipleOriginResultSet.Any())
            {
                return multipleOriginResultSet.First().Value.Items.FirstOrDefault();
            }

            throw CommunicationRepositoryException.PipelineNotFound(tenantId, pipelineRtId);
        }
        catch (Exception e)
        {
            throw CommunicationRepositoryException.CommonFailedGettingPipeline(tenantId, pipelineRtId, e);
        }
    }
}