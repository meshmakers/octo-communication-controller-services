using Meshmakers.Octo.Services.Infrastructure.Services;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

/// <summary>
/// Configuration service of the communication controller.
/// </summary>
public interface IConfigurationService : IDefaultConfigurationCreatorService
{
    /// <summary>
    /// Enables the communication controller for a tenant
    /// </summary>
    /// <param name="tenantId"></param>
    /// <returns></returns>
    Task EnableAsync(string tenantId);
    
    /// <summary>
    /// Disables the communication controller for a tenant
    /// </summary>
    /// <param name="tenantId">Id of the tenant</param>
    /// <returns></returns>
    Task DisableAsync(string tenantId);
    
    /// <summary>
    /// Returns true if the communication controller is enabled for a tenant
    /// </summary>
    /// <param name="tenantId"></param>
    /// <returns></returns>
    Task<bool> IsEnabledAsync(string tenantId);
}