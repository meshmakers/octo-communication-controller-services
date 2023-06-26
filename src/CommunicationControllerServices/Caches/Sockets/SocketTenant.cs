using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Sockets.Descriptions;
using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.Communication.Plugs.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Sockets.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Sockets;

internal class SocketTenant
{
    private readonly ISocketCachePublish _socketCachePublish;
    public string TenantId { get; }
    
    private readonly ConcurrentDictionary<string, Socket> _plugsByConnectId;
    private readonly ConcurrentDictionary<OctoObjectId, Socket> _plugsById;

    public IReadOnlyDictionary<string, Socket> SocketsByConnectionId { get; private set; }
    public IReadOnlyDictionary<OctoObjectId, Socket> SocketsById { get; private set; }

    public SocketTenant(ISocketCachePublish socketCachePublish, string tenantId)
    {
        _socketCachePublish = socketCachePublish;

        TenantId = tenantId;
        _plugsByConnectId = new ConcurrentDictionary<string, Socket>();
        _plugsById = new ConcurrentDictionary<OctoObjectId, Socket>();
        SocketsByConnectionId = new ReadOnlyDictionary<string, Socket>(_plugsByConnectId);
        SocketsById = new ReadOnlyDictionary<OctoObjectId, Socket>(_plugsById);
    }
    
    public SocketTenant(ISocketCachePublish socketCachePublish, string tenantId, IList<SocketDescription> socketDescriptions)
    {
        _socketCachePublish = socketCachePublish;
        
        TenantId = tenantId;

        var plugs = socketDescriptions.Select(p => new Socket(socketCachePublish, p)).ToArray();
        _plugsByConnectId = new ConcurrentDictionary<string, Socket>(
            plugs.ToDictionary(p => p.ConnectionId, p => p));
        _plugsById = new ConcurrentDictionary<OctoObjectId, Socket>(
            plugs.ToDictionary(p => p.SocketRtId, p => p));
        SocketsByConnectionId = new ReadOnlyDictionary<string, Socket>(_plugsByConnectId);
        SocketsById = new ReadOnlyDictionary<OctoObjectId, Socket>(_plugsById);
    }

    public Socket AddSocket(OctoObjectId plugRtId, string connectionId, SocketConfigurationDto configuration)
    {
        var plug = new Socket(_socketCachePublish, plugRtId, connectionId, configuration);
        _plugsByConnectId.AddOrUpdate(connectionId, _ => plug,
            (_, _) => plug);
        _plugsById.AddOrUpdate(plugRtId, _ => plug,
            (_, _) => plug);
        
        _socketCachePublish.PublishConfiguration();

        return plug;
    }

    public void RemoveSocket(OctoObjectId plugRtId)
    {
        if (_plugsById.TryRemove(plugRtId, out var plug))
        {
            if (_plugsByConnectId.TryRemove(plug.ConnectionId, out _))
            {
                _socketCachePublish.PublishConfiguration();
            }
        }
    }

    public SocketTenantDescription GetTenantDescription()
    {
        return new SocketTenantDescription
        {
            TenantId = TenantId,
            Sockets = SocketsById.Values.Select(p => p.GetPlugDescription()).ToArray()
        };
    }

    public void Clear()
    {
        _plugsByConnectId.Clear();
        _plugsById.Clear();
        
        _socketCachePublish.PublishConfiguration();
    }
}