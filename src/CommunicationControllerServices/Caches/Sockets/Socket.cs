using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Services.Common.DistributionEventHub.Messages.Payloads;

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

    public void UpdateConfiguration(string tenantId, SocketConfigurationDto socketConfigurationDto)
    {
        Configuration = socketConfigurationDto;
        _socketCachePublish.PublishConfiguration(tenantId);
    }

    public void UpdateConnectionId(string connectionId)
    {
        ConnectionId = connectionId;
    }

    public SocketDescription GetSocketDescription()
    {
        return new SocketDescription(SocketRtId, ConnectionId, Configuration);
    }
}