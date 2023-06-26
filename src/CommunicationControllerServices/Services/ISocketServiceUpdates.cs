namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

internal interface ISocketServiceUpdates : ISocketService
{
    /// <summary>
    /// Reloads an entire tenant
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <returns></returns>
    Task ReloadTenantAsync(string tenantId);
}