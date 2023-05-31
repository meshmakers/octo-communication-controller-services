namespace Meshmakers.Octo.Backend.PlugControllerServices.Models;

public class PlugHubTenantDescription
{
    public string TenantId { get; set; }= null!;
    public IEnumerable<PlugHubPoolDescription> Pools { get; set; } = null!;
}