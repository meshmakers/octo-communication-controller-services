using Meshmakers.Octo.Backend.PlugControllerServices.Caches.Plugs.Descriptions;
using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.Communication.Plugs.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Backend.PlugControllerServices.Caches.Plugs;

internal class Plug
{
    private readonly IPlugCachePublish _plugCachePublish;

    public Plug(IPlugCachePublish plugCachePublish, OctoObjectId plugRtId, string connectionId, PlugConfigurationDto configuration)
    {
        _plugCachePublish = plugCachePublish;
        PlugRtId = plugRtId;
        ConnectionId = connectionId;
        Configuration = configuration;
    }

    public Plug(IPlugCachePublish plugCachePublish, PlugDescription plugHubPoolDescription)
        : this(plugCachePublish, plugHubPoolDescription.PlugRtId, plugHubPoolDescription.ConnectionId, plugHubPoolDescription.Configuration)
    {
    }

    public OctoObjectId PlugRtId { get; }

    public string ConnectionId { get; private set; }
    
    public PlugConfigurationDto Configuration { get; private set; }

    public void UpdateConfiguration(PlugConfigurationDto plugConfigurationDto)
    {
        Configuration = plugConfigurationDto;
        _plugCachePublish.PublishConfiguration();
    }

    public void UpdateConnectionId(string connectionId)
    {
        ConnectionId = connectionId;
    }

    public PlugDescription GetPlugDescription()
    {
        return new PlugDescription(PlugRtId, ConnectionId, Configuration);
    }
}