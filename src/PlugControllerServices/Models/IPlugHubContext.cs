namespace Meshmakers.Octo.Backend.PlugControllerServices.Models;

public interface IPlugHubContext
{
    Task InitializeAsync();
    PlugHubTenant AddOrUpdateTenant(string tenantId);
    void RemoveTenant(string tenantId);
    PlugHubTenant? TryGetTenant(string tenantId);
    void PublishConfiguration();
}