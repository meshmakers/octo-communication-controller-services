using Meshmakers.Octo.Backend.CommunicationControllerServices.Options;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Services.Common.DistributionEventHub.Commands;
using Meshmakers.Octo.Services.Infrastructure.Services;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

internal class DefaultConfigurationCreatorService : IDefaultConfigurationCreatorService
{
    private readonly ISystemContext _systemContext;

    public DefaultConfigurationCreatorService(ISystemContext systemContext, 
        IOptions<CommunicationControllerOptions> octoBotServicesOptions)
    {
        _systemContext = systemContext;
    }
    
    public Task SetupAsync(string tenantId)
    {
        throw new NotImplementedException();
    }
}