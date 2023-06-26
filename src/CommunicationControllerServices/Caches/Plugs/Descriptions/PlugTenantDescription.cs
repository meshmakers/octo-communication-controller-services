namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Plugs.Descriptions;

internal class PlugTenantDescription
{
    public IEnumerable<PlugDescription> Plugs { get; set; } = null!;
    public string TenantId { get; set; } = null!;
}