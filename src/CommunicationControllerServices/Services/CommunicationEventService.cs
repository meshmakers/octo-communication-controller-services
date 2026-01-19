using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Services.Notifications;
using Meshmakers.Octo.Services.Notifications.Generated.System.Notification.v2;
using Meshmakers.Octo.Services.Notifications.Services;
using NLog;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

/// <summary>
/// Implementation of ICommunicationEventService that handles scoped IEventRepository access
/// for singleton services.
/// </summary>
internal class CommunicationEventService : ICommunicationEventService
{
    private readonly IServiceProvider _serviceProvider;
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    public CommunicationEventService(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public async Task StoreEventAsync(string tenantId, RtEventLevelsEnum level, string message,
        RtEntityId? associatedRtEntityId = null)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var eventRepository = scope.ServiceProvider.GetRequiredService<IEventRepository>();
            await eventRepository.StoreEventAsync(tenantId, RtEventSourcesEnum.CommunicationService, level, message,
                associatedRtEntityId);
        }
        catch (EventStoreFailedException ex)
        {
            Logger.Warn(ex, "[{TenantId}] Failed to store event (level: {Level}, message: '{Message}'). " +
                           "Event storage failures do not affect core functionality.",
                tenantId, level, message);
        }
    }

    public async Task StoreInformationEventAsync(string tenantId, string message,
        RtEntityId? associatedRtEntityId = null)
    {
        await StoreEventAsync(tenantId, RtEventLevelsEnum.Information, message, associatedRtEntityId);
    }

    public async Task StoreWarningEventAsync(string tenantId, string message, RtEntityId? associatedRtEntityId = null)
    {
        await StoreEventAsync(tenantId, RtEventLevelsEnum.Warning, message, associatedRtEntityId);
    }

    public async Task StoreErrorEventAsync(string tenantId, string message, RtEntityId? associatedRtEntityId = null)
    {
        await StoreEventAsync(tenantId, RtEventLevelsEnum.Error, message, associatedRtEntityId);
    }

    public void StoreSystemInformationEvent(string message)
    {
        using var scope = _serviceProvider.CreateScope();
        var eventRepository = scope.ServiceProvider.GetRequiredService<IEventRepository>();
        eventRepository.StoreSystemInformationEvent(RtEventSourcesEnum.CommunicationService, message);
    }

    public void StoreSystemWarningEvent(string message)
    {
        using var scope = _serviceProvider.CreateScope();
        var eventRepository = scope.ServiceProvider.GetRequiredService<IEventRepository>();
        eventRepository.StoreSystemWarningEvent(RtEventSourcesEnum.CommunicationService, message);
    }

    public void StoreSystemErrorEvent(string message)
    {
        using var scope = _serviceProvider.CreateScope();
        var eventRepository = scope.ServiceProvider.GetRequiredService<IEventRepository>();
        eventRepository.StoreSystemErrorEvent(RtEventSourcesEnum.CommunicationService, message);
    }
}
