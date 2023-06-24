using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.Communication.Plugs.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Plugs.Descriptions;

internal class PlugDescription
{
    public string ConnectionId { get; }
    public OctoObjectId PlugRtId { get; }
    public PlugConfigurationDto Configuration { get; }
    

    public PlugDescription(OctoObjectId plugRtId, string connectionId, PlugConfigurationDto configuration)
    {
        PlugRtId = plugRtId;
        ConnectionId = connectionId;
        Configuration = configuration;
    }
}