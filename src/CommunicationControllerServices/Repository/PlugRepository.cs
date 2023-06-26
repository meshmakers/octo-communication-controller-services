using Meshmakers.Octo.Backend.CommunicationControllerServices.CkModelEntities;
using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.Communication.Plugs.Contracts.DataTransferObjects;
using Meshmakers.Octo.SystematizedData.Persistence;
using Meshmakers.Octo.SystematizedData.Persistence.DataAccess;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;

/// <summary>
/// Repository for pool related operations
/// </summary>
public class PlugRepository : IPlugRepository
{
    private readonly ISystemContext _systemContext;


    /// <summary>
    /// Constructor
    /// </summary>
    /// <param name="systemContext">The root object of the persistence layer</param>
    public PlugRepository(ISystemContext systemContext)
    {
        _systemContext = systemContext;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyCollection<RtPlug>> GetPlugsAsync(string tenantId, OctoObjectId poolRtId)
    {
        var tenantContext = await _systemContext.CreateOrGetTenantContextAsync(tenantId);

        var session = await tenantContext.Repository.StartSessionAsync();
        try
        {
            session.StartTransaction();

            var plugResultSet = await tenantContext.Repository.GetRtAssociationTargetsAsync<RtCommunicationPool, RtPlug>(session,
                new[] { poolRtId.ToObjectId() },
                Statics.RoleIdParentChild, GraphDirections.Inbound, null, new DataQueryOperation());

            if (!plugResultSet.Any())
            {
                PlugRepositoryException.PoolNotFound(tenantId, poolRtId);
            }

            var list = plugResultSet.First().Value.Result.ToList();

            await session.CommitTransactionAsync();

            return list;
        }
        catch (Exception e)
        {
            throw PlugRepositoryException.CommonFailedGettingPlugs(tenantId, poolRtId, e);
        }
    }

    /// <inheritdoc />
    public async Task<RtPlug> GetPlugAsync(string tenantId, OctoObjectId plugRtId)
    {
        var tenantContext = await _systemContext.CreateOrGetTenantContextAsync(tenantId);

        var session = await tenantContext.Repository.StartSessionAsync();
        try
        {
            session.StartTransaction();

            var rtPlug = await tenantContext.Repository.GetRtEntityAsync<RtPlug>(session, plugRtId);

            if (rtPlug == null)
            {
                throw PlugRepositoryException.PlugNotFound(tenantId, plugRtId);
            }

            await session.CommitTransactionAsync();

            return rtPlug;
        }
        catch (Exception e)
        {
            throw PlugRepositoryException.CommonFailedGettingPlug(tenantId, plugRtId, e);
        }
    }

    public async Task<bool> IsTenantExistingAsync(string tenantId)
    {
        var systemSession = await _systemContext.StartSystemSessionAsync();

        try
        {
            systemSession.StartTransaction();

            var isTenantExisting = await _systemContext.IsTenantExistingAsync(systemSession, tenantId);

            await systemSession.CommitTransactionAsync();

            return isTenantExisting;
        }
        catch (Exception e)
        {
            throw PlugRepositoryException.CommonFailedIsTenantExisting(tenantId,  e);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyCollection<RtPlug>> GetPlugsAsync(string tenantId)
    {
        var tenantContext = await _systemContext.CreateOrGetTenantContextAsync(tenantId);

        var session = await tenantContext.Repository.StartSessionAsync();
        try
        {
            session.StartTransaction();

            var resultSet = await tenantContext.Repository.GetRtEntitiesByTypeAsync<RtPlug>(session, new DataQueryOperation());

            await session.CommitTransactionAsync();

            return resultSet.Result.ToList();
        }
        catch (Exception e)
        {
            throw PlugRepositoryException.CommonFailedGettingPlugs(tenantId, e);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyCollection<RtCommunicationPool>> GetPoolByNameAsync(string tenantId, string poolName)
    {
        var tenantContext = await _systemContext.CreateOrGetTenantContextAsync(tenantId);

        var session = await tenantContext.Repository.StartSessionAsync();
        try
        {
            session.StartTransaction();

            var poolResultSet = await tenantContext.Repository.GetRtEntitiesByTypeAsync<RtCommunicationPool>(session,
                new DataQueryOperation
                    { FieldFilters = new[] { new FieldFilter(nameof(RtCommunicationPool.Name), FieldFilterOperator.Equals, poolName) } });

            await session.CommitTransactionAsync();

            return poolResultSet.Result.ToList();
        }
        catch (Exception e)
        {
            throw PlugRepositoryException.CommonFailedGettingPoolByName(tenantId, poolName, e);
        }
    }

    /// <inheritdoc />
    public async Task CreatePoolAsync(string tenantId, string poolName)
    {
        var tenantContext = await _systemContext.CreateOrGetTenantContextAsync(tenantId);

        var session = await tenantContext.Repository.StartSessionAsync();
        try
        {
            session.StartTransaction();

            var rtPlugEntity = new RtCommunicationPool
            {
                State = PoolStates.Created,
                Name = poolName
            };

            var entityUpdateInfoList = new List<EntityUpdateInfo>
            {
                new(rtPlugEntity, EntityModOptions.Create)
            };

            await tenantContext.Repository.ApplyChanges(session, entityUpdateInfoList);

            await session.CommitTransactionAsync();
        }
        catch (Exception e)
        {
            throw PlugRepositoryException.CommonFailedCreatePool(tenantId, poolName, e);
        }
    }

    /// <inheritdoc />
    public async Task SetPoolStateAsync(string tenantId, OctoObjectId poolRtId, PoolStates state)
    {
        var tenantContext = await _systemContext.CreateOrGetTenantContextAsync(tenantId);

        var session = await tenantContext.Repository.StartSessionAsync();
        try
        {
            session.StartTransaction();

            var rtPlugEntity = new RtCommunicationPool
            {
                RtId = poolRtId.ToObjectId(),
                State = state
            };

            var entityUpdateInfoList = new List<EntityUpdateInfo>
            {
                new(rtPlugEntity, EntityModOptions.Update)
            };

            await tenantContext.Repository.ApplyChanges(session, entityUpdateInfoList);

            await session.CommitTransactionAsync();
        }
        catch (Exception e)
        {
            throw PlugRepositoryException.CommonFailedSetPoolState(tenantId, poolRtId, state, e);
        }
    }

    /// <inheritdoc />
    public async Task SetPlugStateAsync(string tenantId, OctoObjectId plugRtId, PlugStates state)
    {
        var tenantContext = await _systemContext.CreateOrGetTenantContextAsync(tenantId);

        var session = await tenantContext.Repository.StartSessionAsync();
        try
        {
            session.StartTransaction();

            var rtPlugEntity = new RtPlug
            {
                RtId = plugRtId.ToObjectId(),
                State = state
            };

            var entityUpdateInfoList = new List<EntityUpdateInfo>
            {
                new(rtPlugEntity, EntityModOptions.Update)
            };

            await tenantContext.Repository.ApplyChanges(session, entityUpdateInfoList);

            await session.CommitTransactionAsync();
        }
        catch (Exception e)
        {
            throw PlugRepositoryException.CommonFailedSetPlugState(tenantId, plugRtId, state, e);
        }
    }

    /// <inheritdoc />
    public async Task<RtCommunicationPool> GetPoolOfPlugAsync(string tenantId, OctoObjectId plugRtId)
    {
        var tenantContext = await _systemContext.CreateOrGetTenantContextAsync(tenantId);

        var session = await tenantContext.Repository.StartSessionAsync();
        try
        {
            session.StartTransaction();

            var poolResultSet = await tenantContext.Repository.GetRtAssociationTargetsAsync<RtPlug, RtCommunicationPool>(session,
                new[] { plugRtId.ToObjectId() }, Statics.RoleIdParentChild, GraphDirections.Inbound, null, new DataQueryOperation());

            await session.CommitTransactionAsync();

            if (poolResultSet.Any())
            {
                var pool = poolResultSet.First().Value.Result.FirstOrDefault();
                if (pool != null)
                {
                    return pool;
                }

                throw PlugRepositoryException.PlugNotAssociatedToPool(tenantId, plugRtId);
            }

            throw PlugRepositoryException.PlugNotFound(tenantId, plugRtId);
        }
        catch (Exception e)
        {
            throw PlugRepositoryException.CommonGettingPoolOfPlug(tenantId, plugRtId, e);
        }
    }

    /// <inheritdoc />
    public async Task<RtPlug> GetPlugByMappingAsync(string tenantId, OctoObjectId plugMappingRtId)
    {
        var tenantContext = await _systemContext.CreateOrGetTenantContextAsync(tenantId);

        var session = await tenantContext.Repository.StartSessionAsync();
        try
        {
            session.StartTransaction();

            var poolResultSet = await tenantContext.Repository.GetIndirectRtAssociationTargetsAsync<RtPlugMapping, RtPlug>(session,
                plugMappingRtId.ToObjectId(), Statics.RoleIdParentChild, GraphDirections.Inbound);

            await session.CommitTransactionAsync();

            if (poolResultSet != null)
            {
                if (poolResultSet.Result.Any())
                {
                    return poolResultSet.Result.First();
                }

                throw PlugRepositoryException.PlugMappingNotAssociatedToPlug(tenantId, plugMappingRtId);
            }

            throw PlugRepositoryException.PlugMappingNotFound(tenantId, plugMappingRtId);
        }
        catch (Exception e)
        {
            throw PlugRepositoryException.CommonGettingPlugByMapping(tenantId, plugMappingRtId, e);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyCollection<GroupConfigurationDto>> GetPlugGroupConfigurationAsync(string tenantId, OctoObjectId plugRtId)
    {
        var tenantContext = await _systemContext.CreateOrGetTenantContextAsync(tenantId);

        var session = await tenantContext.Repository.StartSessionAsync();
        try
        {
            session.StartTransaction();

            var plugGroupResultSet = await tenantContext.Repository.GetRtAssociationTargetsAsync<RtPlug, RtPlugGroup>(session,
                new[] { plugRtId.ToObjectId() }, Statics.RoleIdParentChild, GraphDirections.Inbound, null, new DataQueryOperation());

            if (!plugGroupResultSet.ContainsKey(plugRtId.ToObjectId()))
            {
                throw PlugRepositoryException.PlugNotFound(tenantId, plugRtId);
            }

            var plugGroups = plugGroupResultSet[plugRtId.ToObjectId()];

            var groupResultSet = await tenantContext.Repository.GetRtAssociationTargetsAsync<RtPlugGroup, RtPlugMapping>(session,
                plugGroups.Result.Select(x => x.RtId), Statics.RoleIdParentChild, GraphDirections.Inbound, null, new DataQueryOperation());

            var plugGroupConfigurations = new List<GroupConfigurationDto>();
            foreach (var plugGroup in plugGroups.Result)
            {
                if (groupResultSet.TryGetValue(plugGroup.RtId, out var mappingSet))
                {
                    var mappings = new List<MappingConfigurationDto>();
                    foreach (var mapping in mappingSet.Result)
                    {
                        if (!string.IsNullOrWhiteSpace(mapping.Designation) && !string.IsNullOrWhiteSpace(mapping.Configuration))
                        {
                            mappings.Add(new MappingConfigurationDto
                            {
                                Name = mapping.Designation,
                                Id = mapping.RtId.ToOctoObjectId(),
                                Configuration = mapping.Configuration
                            });
                        }
                    }

                    var groupConfiguration = new GroupConfigurationDto
                    {
                        Id = plugGroup.RtId.ToOctoObjectId(),
                        Name = plugGroup.Designation!,
                        Mappings = mappings
                    };
                    plugGroupConfigurations.Add(groupConfiguration);
                }
            }

            return plugGroupConfigurations;
        }
        catch (Exception e)
        {
            throw PlugRepositoryException.CommonGettingPlugGroupsOfPlug(tenantId, plugRtId, e);
        }
    }

    /// <inheritdoc />
    public async Task<RtPlug> GetPlugByGroupAsync(string tenantId, OctoObjectId plugGroupRtId)
    {
        var tenantContext = await _systemContext.CreateOrGetTenantContextAsync(tenantId);

        var session = await tenantContext.Repository.StartSessionAsync();
        try
        {
            session.StartTransaction();

            var poolResultSet = await tenantContext.Repository.GetIndirectRtAssociationTargetsAsync<RtPlugGroup, RtPlug>(session,
                plugGroupRtId.ToObjectId(), Statics.RoleIdParentChild, GraphDirections.Inbound);

            await session.CommitTransactionAsync();

            if (poolResultSet != null)
            {
                if (poolResultSet.Result.Any())
                {
                    return poolResultSet.Result.First();
                }

                throw PlugRepositoryException.PlugGroupNotAssociatedToPlug(tenantId, plugGroupRtId);
            }

            throw PlugRepositoryException.PlugGroupNotFound(tenantId, plugGroupRtId);
        }
        catch (Exception e)
        {
            throw PlugRepositoryException.CommonGettingPlugByGroup(tenantId, plugGroupRtId, e);
        }
    }
}