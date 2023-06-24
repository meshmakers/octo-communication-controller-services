
using Meshmakers.Octo.SystematizedData.Persistence;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

internal class MonitoringService: BackgroundService
{
    private readonly IConfigurationService _configurationService;
    private readonly ISystemContext _systemContext;

    public MonitoringService(IConfigurationService configurationService, ISystemContext systemContext)
    {
        _configurationService = configurationService;
        _systemContext = systemContext;
    }
    
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var communicationControllerStatusDtos = await _configurationService.ReadConfig();

        foreach (var communicationControllerStatusDto in communicationControllerStatusDtos.Where(x=> x.IsEnabled))
        {
            
        }
    }
}