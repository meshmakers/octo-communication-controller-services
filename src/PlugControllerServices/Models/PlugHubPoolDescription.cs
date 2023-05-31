using Meshmakers.Octo.Common.Shared;

namespace Meshmakers.Octo.Backend.PlugControllerServices.Models;

public class PlugHubPoolDescription
{
    public string PoolName { get; set; } = null!;
    public OctoObjectId PlugPoolRtId { get; set; }

    public string ConnectionId { get; set; } = null!;
}