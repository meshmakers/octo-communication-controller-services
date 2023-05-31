using Meshmakers.Octo.Backend.PlugControllerServices.CkModelEntities;
using Meshmakers.Octo.Backend.PlugControllerServices.Models;
using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.Communication.Plugs.Contracts.DataTransferObjects;
using Meshmakers.Octo.SystematizedData.Persistence;
using Meshmakers.Octo.SystematizedData.Persistence.DataAccess;
using Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;
using NLog;

namespace Meshmakers.Octo.Backend.PlugControllerServices.Services;

internal class PlugService : IPlugService
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private readonly ISystemContext _systemContext;
    private readonly Dictionary<string, OctoObjectId> _plugConnections = new();

    public PlugService(ISystemContext systemContext)
    {
        _systemContext = systemContext;
    }

    public async Task<PlugConfigurationDto> RegisterPlug(string tenantId, OctoObjectId plugObjectId, string connectionId)
    {
        Logger.Info("[{TenantId}] Plug '{PlugRtId}' registered with connection id '{ConnectionId}'",
            tenantId, plugObjectId, connectionId);

        _plugConnections[connectionId] = plugObjectId;
        await SetPlugInState(tenantId, plugObjectId, PlugStates.Offline);

        return await GetPlugConfiguration(tenantId, plugObjectId);
    }

    public async Task PlugUnRegistered(string tenantId, OctoObjectId plugObjectId, string connectionId)
    {
        Logger.Info("[{TenantId}] Plug '{PlugRtId}' unregistered with connection id '{ConnectionId}'",
            tenantId, plugObjectId, connectionId);

        _plugConnections.Remove(connectionId);
        await SetPlugInState(tenantId, plugObjectId, PlugStates.Deployed);
    }

    public async Task PlugOffline(string tenantId, string connectionId)
    {
        Logger.Info("[{TenantId}] connection id '{ConnectionId}' offline",
            tenantId, connectionId);
        
        if (_plugConnections.TryGetValue(connectionId, out var plugObjectId))
        {
            await SetPlugInState(tenantId, plugObjectId, PlugStates.Offline);
        }
    }

    public async Task PlugOnline(string tenantId, string connectionId)
    {
        Logger.Info("[{TenantId}] connection id '{ConnectionId}' online",
            tenantId, connectionId);
        
        if (_plugConnections.TryGetValue(connectionId, out var plugObjectId))
        {
            await SetPlugInState(tenantId, plugObjectId, PlugStates.Online);
        }
    }

    private async Task SetPlugInState(string tenantId, OctoObjectId plugObjectId, PlugStates plugState)
    {
        var tenantContext = await _systemContext.CreateOrGetTenantContextAsync(tenantId);
        Logger.Info("[{TenantId}] Setting state of plug '{PlugObjectId}' to '{PlugState}'",
            tenantId, plugObjectId, plugState);
        try
        {
            using var session = await tenantContext.Repository.StartSessionAsync();
            session.StartTransaction();

            var plugEntity = await GetPlugInAsync(session, tenantContext, plugObjectId);
            
            var update = new RtEntity
            {
                CkId = plugEntity.CkId,
                RtId = plugEntity.RtId,
            };
            update.SetAttributeValue("State", AttributeValueTypes.Int, (int)plugState);

            await tenantContext.Repository.ApplyChanges(session, new[]
                { new EntityUpdateInfo(update, EntityModOptions.Update) });

            await session.CommitTransactionAsync();
        }
        catch (Exception e)
        {
            Logger.Error(e, "[{TenantId}] Error setting state of plug '{PlugObjectId}' to '{PlugState}'",
                tenantId, plugObjectId, plugState);
            throw new PlugServiceException($"[{tenantId}] Error setting state of plug '{plugObjectId}' to '{plugState}'", e);
        }
    }


    public async Task<PlugConfigurationDto> GetPlugConfiguration(string tenantId, OctoObjectId plugObjectId)
    {

        try
        {
            var tenantContext = await _systemContext.CreateOrGetTenantContextAsync(tenantId);
            var session = await tenantContext.Repository.StartSessionAsync();
            session.StartTransaction();
            
            var plugEntity = await GetPlugInAsync(session, tenantContext, plugObjectId);

            var persistentServerSettings =
                plugEntity.Configuration?.Deserialize<PersistentServerSettings>() ?? new PersistentServerSettings();

            var plugGroups = await tenantContext.Repository.GetRtAssociationTargetsAsync<RtPlug, RtPlugGroup>(session,
                new[] { plugEntity.RtId }, Statics.RoleIdParentChild, GraphDirections.Inbound, null, new DataQueryOperation());

            var plugGroupConfigurations = new List<GroupConfigurationDto>();
            foreach (var plugGroup in plugGroups[plugEntity.RtId].Result)
            {
                var groupConfiguration = new GroupConfigurationDto
                {
                    Id = plugGroup.RtId.ToOctoObjectId(),
                    Name = plugGroup.Designation!
                };
                plugGroupConfigurations.Add(groupConfiguration);

                var plugMappings = await tenantContext.Repository.GetRtAssociationTargetsAsync<RtPlugGroup, RtPlugMapping>(session,
                    new[] { plugGroup.RtId }, Statics.RoleIdParentChild, GraphDirections.Inbound, null, new DataQueryOperation());

                if (plugMappings.Any())
                {
                    var mappings = new List<MappingConfigurationDto>();
                    foreach (var mapping in plugMappings[plugGroup.RtId].Result)
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

            await session.CommitTransactionAsync();

            var plugConfiguration = new PlugConfigurationDto
            {
                PlugId = plugObjectId,
                ServerConfigurations = new[]
                {
                    new ServerConfigurationDto
                    {
                        Server = persistentServerSettings.Server,
                        Groups = plugGroupConfigurations
                    }
                }
            };
            return plugConfiguration;
        }
        catch (Exception e)
        {
            throw new PlugServiceException("Error during loading of plug configuration", e);
        }
    }


    private async Task<RtPlug> GetPlugInAsync(IOctoSession session, ITenantContext tenantContext,
        OctoObjectId plugObjectId)
    {
        var plugEntity =
            await tenantContext.Repository.GetRtEntityAsync<RtPlug>(session, new RtEntityId(Statics.CkIdPlug, plugObjectId));
        if (plugEntity == null)
        {
            throw new Exception($"Plug {plugObjectId} not found");
        }

        return plugEntity;
    }

    public event EventHandler<UpdatedPlugConfigurationEventArgs>? PlugConfigurationUpdated;


    public async Task<IEnumerable<OctoObjectId>> GetPlugs(string tenantId)
    {
        var tenantContext = await _systemContext.CreateOrGetTenantContextAsync(tenantId);
        
        var session = await tenantContext.Repository.StartSessionAsync();
        session.StartTransaction();
        
        var resultSet = await tenantContext.Repository.GetRtEntitiesByTypeAsync<RtPlug>(session, new DataQueryOperation());

        var result = resultSet.Result.Select(x => x.RtId.ToOctoObjectId());

        await session.CommitTransactionAsync();

        return result;
    }
}