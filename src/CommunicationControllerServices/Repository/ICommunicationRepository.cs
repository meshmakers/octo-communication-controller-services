using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.ConstructionKit.Generated.System.Communication.v1;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;

/// <summary>
/// Repository for pool related operations
/// </summary>
internal interface ICommunicationRepository
{
    /// <summary>
    /// Get all communication adapter from a pool
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="poolRtId">Object id of pool</param>
    /// <returns></returns>
    Task<IReadOnlyCollection<RtCommunicationAdapter>> GetAdaptersAsync(string tenantId, OctoObjectId poolRtId);

    /// <summary>
    /// Gets a list of initialized communication adapter of the given tenant
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <returns></returns>
    Task<IReadOnlyCollection<RtCommunicationAdapter>> GetAdaptersAsync(string tenantId);
    
    /// <summary>
    /// Gets an adapter by object id
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="adapterRtId">Object id of communication adapter</param>
    /// <returns></returns>
    Task<RtCommunicationAdapter> GetAdapterAsync(string tenantId, OctoObjectId adapterRtId);

    /// <summary>
    /// Get pools for a tenant by name
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="poolName">Name of the pool</param>
    /// <returns>List of pools with the given name</returns>
    Task<IReadOnlyCollection<RtCommunicationPool>> GetPoolByNameAsync(string tenantId, string poolName);

    /// <summary>
    /// Creates a pool
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="poolName">Name of pool</param>
    /// <exception cref="CommunicationRepositoryException"></exception>
    Task CreatePoolAsync(string tenantId, string poolName);
    
    /// <summary>
    /// Set the deployment state of a pool
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="poolRtId">Object id of pool</param>
    /// <param name="deploymentState">State of pool</param>
    /// <returns></returns>
    Task SetPoolDeploymentStateAsync(string tenantId, OctoObjectId poolRtId, RtDeploymentStateEnum deploymentState);
    
    /// <summary>
    /// Set the communication state of a pool
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="poolRtId">Object id of pool</param>
    /// <param name="communicationState">State of pool</param>
    /// <returns></returns>
    Task SetPoolCommunicationStateAsync(string tenantId, OctoObjectId poolRtId, RtCommunicationStateEnum communicationState);

    /// <summary>
    /// Set the deployment state of an adapter
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="adapterRtId">Object id of adapter</param>
    /// <param name="deploymentState">State of adapter</param>
    /// <returns></returns>
    Task SetAdapterDeploymentStateAsync(string tenantId, OctoObjectId adapterRtId, RtDeploymentStateEnum deploymentState);
    
    /// <summary>
    /// Set the communication state of an communication adapter
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="adapterRtId">Object id of adapter</param>
    /// <param name="communicationState">State of adapter</param>
    /// <returns></returns>
    Task SetAdapterCommunicationStateAsync(string tenantId, OctoObjectId adapterRtId, RtCommunicationStateEnum communicationState);

    /// <summary>
    /// Gets the pool of an communication adapter
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="adapterRtId">Adapter id</param>
    /// <returns></returns>
    Task<RtCommunicationPool> GetPoolOfAdapterAsync(string tenantId, OctoObjectId adapterRtId);

    /// <summary>
    /// Gets the corresponding adapter of a data pipeline
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="dataPipelineRtId">Object identifier of data pipeline</param>
    /// <returns></returns>
    Task<RtCommunicationAdapter> GetAdapterByDataPipelineAsync(string tenantId, OctoObjectId dataPipelineRtId);

    /// <summary>
    /// Returns true if a tenant exists
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <returns></returns>
    Task<bool> IsTenantExistingAsync(string tenantId);

    /// <summary>
    /// Gets the data pipelines of a communication adapter
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="adapterRtId">Object identifier of communication adapter</param>
    /// <returns></returns>
    Task<IReadOnlyCollection<RtDataPipeline>> GetDataPipelinesAsync(string tenantId, OctoObjectId adapterRtId);
}