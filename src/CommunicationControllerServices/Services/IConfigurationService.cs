using Meshmakers.Octo.Services.Infrastructure.Services;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

internal interface IConfigurationService : IDefaultConfigurationCreatorService
{
    Task TakeDownAsync(string tenantId);
}