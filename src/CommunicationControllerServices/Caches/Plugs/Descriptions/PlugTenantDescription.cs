namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Plugs.Descriptions;

internal class PlugTenantDescription
{
    public IEnumerable<PlugDescription> Plugs { get; set; }
    public string TenantId { get; set; }
}