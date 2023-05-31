using Meshmakers.Octo.Backend.PlugControllerServices.CkModelEntities;
using Meshmakers.Octo.Common.Shared;
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
            throw new PlugRepositoryException("Failed to get plugs from plug pool", e);
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
            throw new PlugRepositoryException("Failed to get plug pools by name", e);
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
            throw new PlugRepositoryException("Failed to create a plug pool", e);
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
            throw new PlugRepositoryException("Failed to set state of plug pool", e);
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
                new []{plugRtId.ToObjectId()}, Statics.RoleIdParentChild, GraphDirections.Inbound, null, new DataQueryOperation());

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
}