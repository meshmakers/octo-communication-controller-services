using Meshmakers.Octo.Backend.CommunicationControllerServices.Models;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

internal interface IConfigurationService
{
    Task<IEnumerable<CommunicationControllerStatusDto>> ReadConfig();
    Task WriteConfig(IEnumerable<CommunicationControllerStatusDto> config, string tenantId);
}