using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Services.Notifications.Generated.System.Notification.v1;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

/// <summary>
/// Service for storing system events in the Communication Controller Service.
/// This service handles the scoped lifetime of IEventRepository for singleton services.
/// </summary>
public interface ICommunicationEventService
{
    /// <summary>
    /// Stores a tenant-scoped event.
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="level">The level of the event</param>
    /// <param name="message">The message of the event</param>
    /// <param name="associatedRtEntityId">Optional entity identifier the event is associated to (e.g., Adapter, Pipeline, Pool)</param>
    Task StoreEventAsync(string tenantId, RtEventLevelsEnum level, string message, RtEntityId? associatedRtEntityId = null);

    /// <summary>
    /// Stores a tenant-scoped information event.
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="message">The message of the event</param>
    /// <param name="associatedRtEntityId">Optional entity identifier the event is associated to (e.g., Adapter, Pipeline, Pool)</param>
    Task StoreInformationEventAsync(string tenantId, string message, RtEntityId? associatedRtEntityId = null);

    /// <summary>
    /// Stores a tenant-scoped warning event.
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="message">The message of the event</param>
    /// <param name="associatedRtEntityId">Optional entity identifier the event is associated to (e.g., Adapter, Pipeline, Pool)</param>
    Task StoreWarningEventAsync(string tenantId, string message, RtEntityId? associatedRtEntityId = null);

    /// <summary>
    /// Stores a tenant-scoped error event.
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="message">The message of the event</param>
    /// <param name="associatedRtEntityId">Optional entity identifier the event is associated to (e.g., Adapter, Pipeline, Pool)</param>
    Task StoreErrorEventAsync(string tenantId, string message, RtEntityId? associatedRtEntityId = null);

    /// <summary>
    /// Stores a system-wide information event.
    /// </summary>
    void StoreSystemInformationEvent(string message);

    /// <summary>
    /// Stores a system-wide warning event.
    /// </summary>
    void StoreSystemWarningEvent(string message);

    /// <summary>
    /// Stores a system-wide error event.
    /// </summary>
    void StoreSystemErrorEvent(string message);
}
