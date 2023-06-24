namespace Meshmakers.Octo.Backend.PlugControllerServices.Caches.Pools.Descriptions;

internal class PoolTenantDescription
{
    public string TenantId { get; set; }= null!;
    public IEnumerable<PoolDescription> Pools { get; set; } = null!;
    public IEnumerable<PoolPlugDescription> Plugs { get; set; } = null!;
}