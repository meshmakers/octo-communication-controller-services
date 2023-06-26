using Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Sockets.Descriptions;
using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.Communication.Sockets.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Sockets;

internal class Socket
{
    private readonly ISocketCachePublish _socketCachePublish;

    public Socket(ISocketCachePublish socketCachePublish, OctoObjectId socketRtId, string connectionId, SocketConfigurationDto configuration)
    {
        _socketCachePublish = socketCachePublish;
        SocketRtId = socketRtId;
        ConnectionId = connectionId;
        Configuration = configuration;
    }

    public Socket(ISocketCachePublish socketCachePublish, SocketDescription socketDescription)
        : this(socketCachePublish, socketDescription.SocketRtId, socketDescription.ConnectionId, socketDescription.Configuration)
    {
    }

    public OctoObjectId SocketRtId { get; }

    public string ConnectionId { get; private set; }
    
    public SocketConfigurationDto Configuration { get; private set; }

    public void UpdateConfiguration(SocketConfigurationDto socketConfigurationDto)
    {
        Configuration = socketConfigurationDto;
        _socketCachePublish.PublishConfiguration();
    }

    public void UpdateConnectionId(string connectionId)
    {
        ConnectionId = connectionId;
    }

    public SocketDescription GetPlugDescription()
    {
        return new SocketDescription(SocketRtId, ConnectionId, Configuration);
    }
}