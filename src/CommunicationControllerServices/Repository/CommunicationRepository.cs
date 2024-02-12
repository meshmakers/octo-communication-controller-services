using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
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
    public async Task<IReadOnlyCollection<RtPlug>> GetPlugsAsync(string tenantId, OctoObjectId poolRtId)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        var session = await tenantRepository.GetSessionAsync();
        try
        {
            session.StartTransaction();

            var plugResultSet = await tenantRepository.GetRtAssociationTargetsAsync<RtCommunicationPool, RtPlug>(session,
                new[] { poolRtId },
                SystemCkIds.ParentChild, GraphDirections.Inbound, null, DataQueryOperation.Create());

            if (!plugResultSet.Any())
            {
                CommunicationRepositoryException.PoolNotFound(tenantId, poolRtId);
            }

            var list = plugResultSet.First().Value.Items.ToList();

            await session.CommitTransactionAsync();

            return list;
        }
        catch (Exception e)
        {
            throw CommunicationRepositoryException.CommonFailedGettingPlugs(tenantId, poolRtId, e);
        }
    }

    /// <inheritdoc />
    public async Task<RtSocket> GetSocketAsync(string tenantId, OctoObjectId socketRtId)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        var session = await tenantRepository.GetSessionAsync();
        try
        {
            session.StartTransaction();

            var rtSocket = await tenantRepository.GetRtEntityByRtIdAsync<RtSocket>(session, socketRtId);

            if (rtSocket == null)
            {
                throw CommunicationRepositoryException.SocketNotFound(tenantId, socketRtId);
            }

            await session.CommitTransactionAsync();

            return rtSocket;
        }
        catch (Exception e)
        {
            throw CommunicationRepositoryException.CommonFailedGettingSocket(tenantId, socketRtId, e);
        }
    }

    /// <inheritdoc />
    public async Task<RtPlug> GetPlugAsync(string tenantId, OctoObjectId plugRtId)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        var session = await tenantRepository.GetSessionAsync();
        try
        {
            session.StartTransaction();

            var rtPlug = await tenantRepository.GetRtEntityByRtIdAsync<RtPlug>(session, plugRtId);

            if (rtPlug == null)
            {
                throw CommunicationRepositoryException.PlugNotFound(tenantId, plugRtId);
            }

            await session.CommitTransactionAsync();

            return rtPlug;
        }
        catch (Exception e)
        {
            throw CommunicationRepositoryException.CommonFailedGettingPlug(tenantId, plugRtId, e);
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

    /// <inheritdoc />
    public async Task<IReadOnlyCollection<RtSocket>> GetSocketsAsync(string tenantId, OctoObjectId poolRtId)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        var session = await tenantRepository.GetSessionAsync();
        try
        {
            session.StartTransaction();

            var resultSet = await tenantRepository.GetRtEntitiesByTypeAsync<RtSocket>(session, DataQueryOperation.Create());

            await session.CommitTransactionAsync();

            return resultSet.Items.ToList();
        }
        catch (Exception e)
        {
            throw CommunicationRepositoryException.CommonFailedGettingSockets(tenantId, e);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyCollection<RtPlug>> GetPlugsAsync(string tenantId)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        var session = await tenantRepository.GetSessionAsync();
        try
        {
            session.StartTransaction();

            var resultSet = await tenantRepository.GetRtEntitiesByTypeAsync<RtPlug>(session, DataQueryOperation.Create());

            await session.CommitTransactionAsync();

            return resultSet.Items.ToList();
        }
        catch (Exception e)
        {
            throw CommunicationRepositoryException.CommonFailedGettingPlugs(tenantId, e);
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

    public async Task SetSocketDeploymentStateAsync(string tenantId, OctoObjectId socketRtId, RtDeploymentStateEnum deploymentState)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        var session = await tenantRepository.GetSessionAsync();
        try
        {
            session.StartTransaction();

            var rtSocketEntity = new RtSocket
            {
                RtId = socketRtId,
                DeploymentState = deploymentState
            };

            var entityUpdateInfoList = new List<EntityUpdateInfo<RtSocket>>
            {
                EntityUpdateInfo<RtSocket>.CreateUpdate(rtSocketEntity.ToRtEntityId(), rtSocketEntity)
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
            throw CommunicationRepositoryException.CommonFailedSetSocketDeploymentState(tenantId, socketRtId, deploymentState, e);
        }
    }

    public async Task SetSocketCommunicationStateAsync(string tenantId, OctoObjectId socketRtId, RtCommunicationStateEnum communicationState)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        var session = await tenantRepository.GetSessionAsync();
        try
        {
            session.StartTransaction();

            var rtSocketEntity = new RtSocket
            {
                RtId = socketRtId,
                CommunicationState = communicationState
            };

            var entityUpdateInfoList = new List<EntityUpdateInfo<RtSocket>>
            {
                EntityUpdateInfo<RtSocket>.CreateUpdate(rtSocketEntity.ToRtEntityId(), rtSocketEntity)
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
            throw CommunicationRepositoryException.CommonFailedSetSocketCommunicationState(tenantId, socketRtId, communicationState, e);
        }
    }

    public async Task SetPlugDeploymentStateAsync(string tenantId, OctoObjectId plugRtId, RtDeploymentStateEnum deploymentState)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        var session = await tenantRepository.GetSessionAsync();
        try
        {
            session.StartTransaction();

            var rtPlugEntity = new RtPlug
            {
                RtId = plugRtId,
                DeploymentState = deploymentState
            };

            var entityUpdateInfoList = new List<EntityUpdateInfo<RtPlug>>
            {
                EntityUpdateInfo<RtPlug>.CreateUpdate(rtPlugEntity.ToRtEntityId(), rtPlugEntity)
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
            throw CommunicationRepositoryException.CommonFailedSetPlugDeploymentState(tenantId, plugRtId, deploymentState, e);
        }
    }

    public async Task SetPlugCommunicationStateAsync(string tenantId, OctoObjectId plugRtId, RtCommunicationStateEnum communicationState)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        var session = await tenantRepository.GetSessionAsync();
        try
        {
            session.StartTransaction();

            var rtPlugEntity = new RtPlug
            {
                RtId = plugRtId,
                CommunicationState = communicationState
            };

            var entityUpdateInfoList = new List<EntityUpdateInfo<RtPlug>>
            {
                EntityUpdateInfo<RtPlug>.CreateUpdate(rtPlugEntity.ToRtEntityId(), rtPlugEntity)
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
            throw CommunicationRepositoryException.CommonFailedSetPlugCommunicationState(tenantId, plugRtId, communicationState, e);
        }
    }

    /// <inheritdoc />
    public async Task<RtCommunicationPool> GetPoolOfPlugAsync(string tenantId, OctoObjectId plugRtId)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        var session = await tenantRepository.GetSessionAsync();
        try
        {
            session.StartTransaction();

            var poolResultSet = await tenantRepository.GetRtAssociationTargetsAsync<RtPlug, RtCommunicationPool>(session,
                new[] { plugRtId }, SystemCkIds.ParentChild, GraphDirections.Inbound, null, DataQueryOperation.Create());

            await session.CommitTransactionAsync();

            if (poolResultSet.Any())
            {
                var pool = poolResultSet.First().Value.Items.FirstOrDefault();
                if (pool != null)
                {
                    return pool;
                }

                throw CommunicationRepositoryException.PlugNotAssociatedToPool(tenantId, plugRtId);
            }

            throw CommunicationRepositoryException.PlugNotFound(tenantId, plugRtId);
        }
        catch (Exception e)
        {
            throw CommunicationRepositoryException.CommonGettingPoolOfPlug(tenantId, plugRtId, e);
        }
    }

    /// <inheritdoc />
    public async Task<RtPlug> GetPlugByMappingAsync(string tenantId, OctoObjectId plugMappingRtId)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        var session = await tenantRepository.GetSessionAsync();
        try
        {
            session.StartTransaction();

            var poolResultSet = await tenantRepository.GetIndirectRtAssociationTargetsAsync<RtPlugMapping, RtPlug>(session,
                plugMappingRtId, SystemCkIds.ParentChild, GraphDirections.Inbound);

            await session.CommitTransactionAsync();

            if (poolResultSet != null)
            {
                if (poolResultSet.Items.Any())
                {
                    return poolResultSet.Items.First();
                }

                throw CommunicationRepositoryException.PlugMappingNotAssociatedToPlug(tenantId, plugMappingRtId);
            }

            throw CommunicationRepositoryException.PlugMappingNotFound(tenantId, plugMappingRtId);
        }
        catch (Exception e)
        {
            throw CommunicationRepositoryException.CommonGettingPlugByMapping(tenantId, plugMappingRtId, e);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyCollection<GroupConfigurationDto>> GetPlugGroupConfigurationAsync(string tenantId, OctoObjectId plugRtId)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        var session = await tenantRepository.GetSessionAsync();
        try
        {
            session.StartTransaction();

            var plugGroupResultSet = await tenantRepository.GetRtAssociationTargetsAsync<RtPlug, RtPlugGroup>(session,
                new[] { plugRtId }, SystemCkIds.ParentChild, GraphDirections.Inbound, null, DataQueryOperation.Create());

            if (!plugGroupResultSet.ContainsKey(plugRtId))
            {
                throw CommunicationRepositoryException.PlugNotFound(tenantId, plugRtId);
            }

            var plugGroups = plugGroupResultSet[plugRtId];

            var groupResultSet = await tenantRepository.GetRtAssociationTargetsAsync<RtPlugGroup, RtPlugMapping>(session,
                plugGroups.Items.Select(x => x.RtId), SystemCkIds.ParentChild, GraphDirections.Inbound, null,
                DataQueryOperation.Create());

            // TODO: test 
            var mappingRtIds = groupResultSet.Values.SelectMany(x=> x.Items.Select(x=> x.RtId)).ToList();
            var mappingResultSet = await tenantRepository.GetRtAssociationsAsync(session, mappingRtIds, GraphDirections.Outbound,
                SystemCommunicationCkIds.Stream);
            var mappingRtIdToStreamRtId = mappingResultSet.ToDictionary(x => x.OriginRtId, x => x);

            var plugGroupConfigurations = new List<GroupConfigurationDto>();
            foreach (var plugGroup in plugGroups.Items)
            {
                if (groupResultSet.TryGetValue(plugGroup.RtId, out var mappingSet))
                {
                    var mappings = new List<MappingConfigurationDto>();
                    foreach (var mapping in mappingSet.Items)
                    {
                        if (mappingRtIdToStreamRtId.TryGetValue(mapping.RtId, out var streamAssociation))
                        {
                            var config = streamAssociation.GetAttributeStringValueOrDefault(SystemCommunicationCkIds
                                .MappingConfigurationAttribute);
                            if (!string.IsNullOrWhiteSpace(mapping.Name) && !string.IsNullOrWhiteSpace(config))
                            {
                                mappings.Add(new MappingConfigurationDto(
                                    mapping.Name,
                                    mapping.RtId,
                                    config));
                            }
                        }
       
                    }

                    var groupConfiguration = new GroupConfigurationDto(
                        plugGroup.Name,
                        plugGroup.RtId,
                        mappings);
                    plugGroupConfigurations.Add(groupConfiguration);
                }
            }

            return plugGroupConfigurations;
        }
        catch (Exception e)
        {
            throw CommunicationRepositoryException.CommonGettingPlugGroupsOfPlug(tenantId, plugRtId, e);
        }
    }

    /// <inheritdoc />
    public async Task<RtPlug> GetPlugByGroupAsync(string tenantId, OctoObjectId plugGroupRtId)
    {
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        var session = await tenantRepository.GetSessionAsync();
        try
        {
            session.StartTransaction();

            var poolResultSet = await tenantRepository.GetIndirectRtAssociationTargetsAsync<RtPlugGroup, RtPlug>(session,
                plugGroupRtId, SystemCkIds.ParentChild, GraphDirections.Inbound);

            await session.CommitTransactionAsync();

            if (poolResultSet != null)
            {
                if (poolResultSet.Items.Any())
                {
                    return poolResultSet.Items.First();
                }

                throw CommunicationRepositoryException.PlugGroupNotAssociatedToPlug(tenantId, plugGroupRtId);
            }

            throw CommunicationRepositoryException.PlugGroupNotFound(tenantId, plugGroupRtId);
        }
        catch (Exception e)
        {
            throw CommunicationRepositoryException.CommonGettingPlugByGroup(tenantId, plugGroupRtId, e);
        }
    }
}