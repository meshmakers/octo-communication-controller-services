using Meshmakers.Octo.Backend.CommunicationControllerServices.CkModelEntities;
using Meshmakers.Octo.SystematizedData.Persistence.DataAccess;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

internal interface IPoolServiceUpdates : IPoolService
{
    /// <summary>
    /// Handles a pool update
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="info">Update information object</param>
    /// <returns></returns>
    Task OnHandlePoolUpdateAsync(string tenantId, UpdateInfo<RtCommunicationPool> info);

    /// <summary>
    /// Handles a plug update
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="info">Update information object</param>
    /// <returns></returns>
    Task OnHandlePlugUpdateAsync(string tenantId, UpdateInfo<RtPlug> info);
}