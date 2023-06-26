using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.Communication.Sockets.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Sockets.Descriptions;

internal class SocketDescription
{
    public string ConnectionId { get; }
    public OctoObjectId SocketRtId { get; }
    public SocketConfigurationDto Configuration { get; }
    

    public SocketDescription(OctoObjectId socketRtId, string connectionId, SocketConfigurationDto configuration)
    {
        SocketRtId = socketRtId;
        ConnectionId = connectionId;
        Configuration = configuration;
    }
}