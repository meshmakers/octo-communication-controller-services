
using Meshmakers.Octo.SystematizedData.Persistence;

namespace Meshmakers.Octo.Backend.PlugControllerServices.Services;

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
        var plugControllerStatusDtos = await _configurationService.ReadConfig();

        foreach (var plugControllerStatusDto in plugControllerStatusDtos.Where(x=> x.IsEnabled))
        {
            
        }
    }
}