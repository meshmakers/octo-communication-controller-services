using Meshmakers.Octo.ConstructionKit.Models.System.Communication.ConstructionKit.Generated.System.Communication.v1;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repository;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

internal interface IPoolServiceUpdates : IPoolService
{
    /// <summary>
    /// Handles a pool update
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="info">Update information object</param>
    /// <returns></returns>
    Task OnHandlePoolUpdateAsync(string tenantId, IUpdateInfo<RtCommunicationPool> info);

    /// <summary>
    /// Handles a adapter update
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="info">Update information object</param>
    /// <returns></returns>
    Task OnHandleAdapterUpdateAsync(string tenantId, IUpdateInfo<RtCommunicationAdapter> info);
}