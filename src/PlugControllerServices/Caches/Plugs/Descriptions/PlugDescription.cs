using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.Communication.Plugs.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Backend.PlugControllerServices.Caches.Plugs.Descriptions;

public class PlugDescription
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