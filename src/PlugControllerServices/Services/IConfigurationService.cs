using Meshmakers.Octo.Backend.PlugControllerServices.Models;

namespace Meshmakers.Octo.Backend.PlugControllerServices.Services;

public interface IConfigurationService
{
    Task<IEnumerable<PlugControllerStatusDto>> ReadConfig();
    Task WriteConfig(IEnumerable<PlugControllerStatusDto> config, string tenantId);
}