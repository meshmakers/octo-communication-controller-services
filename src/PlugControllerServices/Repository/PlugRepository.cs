using Meshmakers.Octo.Backend.PlugControllerServices.CkModelEntities;
using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.Communication.Plugs.Contracts.DataTransferObjects;
using Meshmakers.Octo.SystematizedData.Persistence;
using Meshmakers.Octo.SystematizedData.Persistence.DataAccess;

namespace Meshmakers.Octo.Backend.PlugControllerServices.Repository;

/// <summary>
/// Repository for plug pool related operations
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
    public async Task<IReadOnlyCollection<RtPlug>> GetPlugsAsync(string tenantId, OctoObjectId plugPoolId)
    {
        var tenantContext = await _systemContext.CreateOrGetTenantContextAsync(tenantId);

        var session = await tenantContext.Repository.StartSessionAsync();
        try
        {
            session.StartTransaction();

            var plugResultSet = await tenantContext.Repository.GetRtAssociationTargetsAsync<RtPlugPool, RtPlug>(session,
                new[] { plugPoolId.ToObjectId() },
                Statics.RoleIdParentChild, GraphDirections.Inbound, null, new DataQueryOperation());

            if (!plugResultSet.Any())
            {
                PlugRepositoryException.PlugPoolNotFound(tenantId, plugPoolId);
            }

            var list = plugResultSet.First().Value.Result.ToList();

            await session.CommitTransactionAsync();

            return list;
        }
        catch (Exception e)
        {
            throw PlugRepositoryException.CommonFailedGettingPlugs(tenantId, plugPoolId, e);
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
    public async Task<IReadOnlyCollection<RtPlugPool>> GetPlugPoolByNameAsync(string tenantId, string plugPoolName)
    {
        var tenantContext = await _systemContext.CreateOrGetTenantContextAsync(tenantId);

        var session = await tenantContext.Repository.StartSessionAsync();
        try
        {
            session.StartTransaction();

            var plugPoolResultSet = await tenantContext.Repository.GetRtEntitiesByTypeAsync<RtPlugPool>(session,
                new DataQueryOperation
                    { FieldFilters = new[] { new FieldFilter(nameof(RtPlugPool.Name), FieldFilterOperator.Equals, plugPoolName) } });

            await session.CommitTransactionAsync();

            return plugPoolResultSet.Result.ToList();
        }
        catch (Exception e)
        {
            throw PlugRepositoryException.CommonFailedGettingPlugPoolByName(tenantId, plugPoolName, e);
        }
    }

    /// <inheritdoc />
    public async Task CreatePlugPoolAsync(string tenantId, string poolName)
    {
        var tenantContext = await _systemContext.CreateOrGetTenantContextAsync(tenantId);

        var session = await tenantContext.Repository.StartSessionAsync();
        try
        {
            session.StartTransaction();

            var rtPlugEntity = new RtPlugPool
            {
                State = PlugPoolStates.Created,
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
            throw PlugRepositoryException.CommonFailedCreatePlugPool(tenantId, poolName, e);
        }
    }

    /// <inheritdoc />
    public async Task SetPlugPoolStateAsync(string tenantId, OctoObjectId plugPoolId, PlugPoolStates state)
    {
        var tenantContext = await _systemContext.CreateOrGetTenantContextAsync(tenantId);

        var session = await tenantContext.Repository.StartSessionAsync();
        try
        {
            session.StartTransaction();

            var rtPlugEntity = new RtPlugPool
            {
                RtId = plugPoolId.ToObjectId(),
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
            throw PlugRepositoryException.CommonFailedSetPlugPoolState(tenantId, plugPoolId, state, e);
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
    public async Task<RtPlugPool> GetPlugPoolOfPlugAsync(string tenantId, OctoObjectId plugRtId)
    {
        var tenantContext = await _systemContext.CreateOrGetTenantContextAsync(tenantId);

        var session = await tenantContext.Repository.StartSessionAsync();
        try
        {
            session.StartTransaction();

            var plugPoolResultSet = await tenantContext.Repository.GetRtAssociationTargetsAsync<RtPlug, RtPlugPool>(session,
                new[] { plugRtId.ToObjectId() }, Statics.RoleIdParentChild, GraphDirections.Inbound, null, new DataQueryOperation());

            await session.CommitTransactionAsync();

            if (plugPoolResultSet.Any())
            {
                var plugPool = plugPoolResultSet.First().Value.Result.FirstOrDefault();
                if (plugPool != null)
                {
                    return plugPool;
                }

                throw PlugRepositoryException.PlugNotAssociatedToPlugPool(tenantId, plugRtId);
            }

            throw PlugRepositoryException.PlugNotFound(tenantId, plugRtId);
        }
        catch (Exception e)
        {
            throw PlugRepositoryException.CommonGettingPlugPoolOfPlug(tenantId, plugRtId, e);
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
                plugGroups.Result.Select(x=> x.RtId), Statics.RoleIdParentChild, GraphDirections.Inbound, null, new DataQueryOperation());
            
            var plugGroupConfigurations = new List<GroupConfigurationDto>();
            foreach (var plugGroup in plugGroups.Result)
            {
                var groupConfiguration = new GroupConfigurationDto
                {
                    Id = plugGroup.RtId.ToOctoObjectId(),
                    Name = plugGroup.Designation!
                };
                plugGroupConfigurations.Add(groupConfiguration);
                
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

                    groupConfiguration.Mappings = mappings;
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