using Meshmakers.Octo.Backend.PlugControllerServices.Models;

namespace Meshmakers.Octo.Backend.PlugControllerServices.Services;

internal interface IConfigurationService
{
    Task<IEnumerable<PlugControllerStatusDto>> ReadConfig();
    Task WriteConfig(IEnumerable<PlugControllerStatusDto> config, string tenantId);
    TenantDescription GetOrAddTenant(string tenantId);
}