using Meshmakers.Octo.Backend.CommunicationControllerServices.CkModelEntities;
using Meshmakers.Octo.SystematizedData.Persistence.DataAccess;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

internal interface IPlugServiceUpdates : IPlugService
{
    /// <summary>
    /// Reloads an entire tenant
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <returns></returns>
    Task ReloadTenantAsync(string tenantId);
    
    /// <summary>
    /// Handles an update of a plug mapping
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="info">Update information object</param>
    /// <returns></returns>
    Task OnHandlePlugMappingUpdateAsync(string tenantId, UpdateInfo<RtPlugMapping> info);

    /// <summary>
    /// Handles an update of a plug group
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="info">Update information object</param>
    /// <returns></returns>
    Task OnHandlePlugGroupUpdateAsync(string tenantId, UpdateInfo<RtPlugGroup> info);

    /// <summary>
    /// Handles an update of a plug
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="info">Update information object</param>
    /// <returns></returns>
    Task OnHandlePlugUpdateAsync(string tenantId, UpdateInfo<RtPlug> info);
}