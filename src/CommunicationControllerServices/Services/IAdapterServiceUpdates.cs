using Meshmakers.Octo.ConstructionKit.Models.System.Communication.ConstructionKit.Generated.System.Communication.v1;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repository;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

internal interface IAdapterServiceUpdates : IAdapterService
{
    /// <summary>
    /// Reloads an entire tenant
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <returns></returns>
    Task ReloadTenantAsync(string tenantId);
    
    /// <summary>
    /// Handles an update of a data pipeline 
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="info">Update information object</param>
    /// <returns></returns>
    Task OnHandleDataPipelineUpdateAsync(string tenantId, IUpdateInfo<RtDataPipeline> info);

    /// <summary>
    /// Handles an update of an adapter
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="info">Update information object</param>
    /// <returns></returns>
    Task OnHandleAdapterUpdateAsync(string tenantId, IUpdateInfo<RtCommunicationAdapter> info);
}