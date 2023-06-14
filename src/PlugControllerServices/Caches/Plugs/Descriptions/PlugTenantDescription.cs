namespace Meshmakers.Octo.Backend.PlugControllerServices.Caches.Plugs.Descriptions;

public class PlugTenantDescription
{
    public IEnumerable<PlugDescription> Plugs { get; set; }
    public string TenantId { get; set; }
}