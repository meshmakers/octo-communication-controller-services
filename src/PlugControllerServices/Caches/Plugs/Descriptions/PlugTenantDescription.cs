namespace Meshmakers.Octo.Backend.PlugControllerServices.Caches.Plugs.Descriptions;

internal class PlugTenantDescription
{
    public IEnumerable<PlugDescription> Plugs { get; set; }
    public string TenantId { get; set; }
}