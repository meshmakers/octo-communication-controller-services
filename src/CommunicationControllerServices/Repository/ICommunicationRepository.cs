using Meshmakers.Octo.Backend.CommunicationControllerServices.Models;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v3;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;

/// <summary>
/// Repository for pool related operations
/// </summary>
public interface ICommunicationRepository
{
    /// <summary>
    /// Get all communication adapter from a pool
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="poolRtId">Object id of pool</param>
    /// <returns></returns>
    Task<IReadOnlyCollection<RtAdapter>> GetAdaptersAsync(string tenantId, OctoObjectId poolRtId);

    /// <summary>
    /// Gets a list of initialized communication adapter of the given tenant
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <returns></returns>
    Task<IReadOnlyCollection<RtAdapter>> GetAdaptersAsync(string tenantId);

    /// <summary>
    /// Gets an adapter by object id
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="adapterRtEntityId">ID of adapter</param>
    /// <returns></returns>
    Task<RtAdapter> GetAdapterAsync(string tenantId, RtEntityId adapterRtEntityId);

    /// <summary>
    /// Gets an adapter by his pipeline
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="pipelineRtEntityId">Object identifier of pipeline</param>
    /// <returns></returns>
    Task<RtAdapter?> GetAdapterByPipelineAsync(string tenantId, RtEntityId pipelineRtEntityId);

    /// <summary>
    /// Get pools for a tenant
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <returns>List of pools of the tenant</returns>
    Task<IReadOnlyCollection<RtPool>> GetPoolsAsync(string tenantId);


    /// <summary>
    /// Get pools for a tenant by name
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="poolName">Name of the pool</param>
    /// <returns>List of pools with the given name</returns>
    Task<IReadOnlyCollection<RtPool>> GetPoolByNameAsync(string tenantId, string poolName);

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
    Task SetPoolCommunicationStateAsync(string tenantId, OctoObjectId poolRtId,
        RtCommunicationStateEnum communicationState);

    /// <summary>
    /// Set the deployment state of an adapter
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="adapterRtEntityId">Object id of adapter</param>
    /// <param name="deploymentState">State of adapter</param>
    /// <returns></returns>
    Task SetAdapterDeploymentStateAsync(string tenantId, RtEntityId adapterRtEntityId,
        RtDeploymentStateEnum deploymentState);

    /// <summary>
    /// Set the deployment state of an adapter
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="adapterRtEntityIds">Object id of adapters</param>
    /// <param name="deploymentState">State of adapter</param>
    /// <returns></returns>
    Task SetAdapterDeploymentStateAsync(string tenantId, ICollection<RtEntityId> adapterRtEntityIds,
        RtDeploymentStateEnum deploymentState);

    /// <summary>
    /// Set the communication state of a communication adapter
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="adapterRtEntityId">Object id of adapter</param>
    /// <param name="communicationState">State of adapter</param>
    /// <returns></returns>
    Task SetAdapterCommunicationStateAsync(string tenantId, RtEntityId adapterRtEntityId,
        RtCommunicationStateEnum communicationState);

    /// <summary>
    /// Gets the pool of a communication adapter
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="adapterRtEntityId">Adapter id</param>
    /// <returns></returns>
    Task<RtPool> GetPoolOfAdapterAsync(string tenantId, RtEntityId adapterRtEntityId);

    /// <summary>
    /// Returns true if a tenant exists
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <returns></returns>
    Task<bool> IsTenantExistingAsync(string tenantId);

    /// <summary>
    /// Gets the pipelines of a communication adapter
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="adapterRtEntityId">Object identifier of communication adapter</param>
    /// <returns></returns>
    Task<IReadOnlyCollection<RtPipeline>> GetPipelinesAsync(string tenantId, RtEntityId adapterRtEntityId);

    /// <summary>
    /// Gets the pipelines of a data flow
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="dataFlowRtId">Object identifier of data flow</param>
    /// <returns></returns>
    Task<IReadOnlyCollection<RtPipeline>> GetPipelinesAsync(string tenantId, OctoObjectId dataFlowRtId);

    /// <summary>
    /// Get the pipeline by id
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="pipelineRtEntityId">Object identifier of pipeline</param>
    /// <returns></returns>
    Task<RtPipeline?> GetPipelineAsync(string tenantId, RtEntityId pipelineRtEntityId);

    /// <summary>
    /// Get the data flow based on the child pipeline
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="pipelineRtId">Object identifier of pipeline</param>
    /// <returns></returns>
    Task<RtDataFlow?> GetDataFlowByPipelineAsync(string tenantId, OctoObjectId pipelineRtId);

    /// <summary>
    /// Get configurations of a pipeline
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="pipelineRtId">Object identifier of pipeline</param>
    /// <returns></returns>
    Task<IEnumerable<RtConfiguration>> GetConfigurationsByPipelineAsync(string tenantId, OctoObjectId pipelineRtId);

    /// <summary>
    /// Gets a list of triggers of the given tenant
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <returns></returns>
    Task<IReadOnlyCollection<RtPipelineTrigger>> GetTriggersAsync(string tenantId);

    /// <summary>
    /// Gets a list of triggers and their pipelines of the given tenant
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <returns></returns>
    Task<IDictionary<RtPipelineTrigger, IList<RtPipeline>>> GetTriggersAndPipelinesAsync(string tenantId);

    /// <summary>
    /// Set the deployment state of a pipeline trigger
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="triggerRtId">Object id of trigger</param>
    /// <param name="deploymentState">State of trigger</param>
    /// <returns></returns>
    Task SetPipelineTriggerDeploymentStateAsync(string tenantId, OctoObjectId triggerRtId,
        RtDeploymentStateEnum deploymentState);

    /// <summary>
    /// Set the deployment state of a pipeline
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="pipelineRtEntityId">Object id of the pipeline</param>
    /// <param name="deploymentState">State of the pipeline</param>
    /// <param name="stateMessage">Optional status message</param>
    /// <returns></returns>
    Task SetPipelineDeploymentStateAsync(string tenantId, RtEntityId pipelineRtEntityId,
        RtDeploymentStateEnum deploymentState, string? stateMessage);

    /// <summary>
    /// Set the pipeline definition YAML of a pipeline
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="pipelineRtEntityId">Object id of the pipeline</param>
    /// <param name="pipelineDefinition">Pipeline definition YAML</param>
    /// <returns></returns>
    Task SetPipelineDefinitionAsync(string tenantId, RtEntityId pipelineRtEntityId,
        string pipelineDefinition);

    /// <summary>
    /// Set the configuration state of an adapter
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="adapterRtEntityId">Object identifier of communication adapter</param>
    /// <param name="configurationState">Configuration state</param>
    /// <param name="stateMessage">An optional status message</param>
    /// <returns></returns>
    Task SetAdapterConfigurationStateAsync(string tenantId, RtEntityId adapterRtEntityId, RtConfigurationStateEnum configurationState, string? stateMessage);

    #region Pipeline Execution

    /// <summary>
    /// Creates a new pipeline execution record
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="execution">The execution entity to create</param>
    /// <param name="pipelineRtEntityId">Pipeline being executed</param>
    /// <param name="adapterRtEntityId">Adapter executing the pipeline</param>
    Task CreatePipelineExecutionAsync(string tenantId, RtPipelineExecution execution,
        RtEntityId pipelineRtEntityId, RtEntityId adapterRtEntityId);

    /// <summary>
    /// Updates an existing pipeline execution record
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="executionId">Execution ID (GUID as string)</param>
    /// <param name="status">New status</param>
    /// <param name="completedAt">Completion timestamp</param>
    /// <param name="durationMs">Duration in milliseconds</param>
    /// <param name="errorMessage">Error message if failed</param>
    Task UpdatePipelineExecutionAsync(string tenantId, string executionId,
        RtPipelineExecutionStatusEnum status, DateTime? completedAt, int? durationMs, string? errorMessage);

    /// <summary>
    /// Gets a pipeline execution by its execution ID
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="executionId">Execution ID (GUID as string)</param>
    /// <returns>The execution or null if not found</returns>
    Task<RtPipelineExecution?> GetPipelineExecutionAsync(string tenantId, string executionId);

    /// <summary>
    /// Gets pipeline executions for a specific pipeline
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="pipelineRtEntityId">Pipeline identifier</param>
    /// <param name="from">Optional start date filter</param>
    /// <param name="to">Optional end date filter</param>
    /// <param name="limit">Optional result limit</param>
    /// <returns>List of executions</returns>
    Task<IReadOnlyList<RtPipelineExecution>> GetPipelineExecutionsAsync(string tenantId,
        RtEntityId pipelineRtEntityId, DateTime? from, DateTime? to, int? limit);

    /// <summary>
    /// Gets a page of pipeline executions for a specific pipeline.
    /// Uses skip/take pagination which triggers the optimized MongoDB query path
    /// with $limit inside $lookup, avoiding the 16MB BSON document size limit.
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="pipelineRtEntityId">Pipeline identifier</param>
    /// <param name="from">Optional start date filter</param>
    /// <param name="to">Optional end date filter</param>
    /// <param name="skip">Number of results to skip</param>
    /// <param name="take">Number of results to take</param>
    /// <returns>List of executions for the requested page</returns>
    Task<IReadOnlyList<RtPipelineExecution>> GetPipelineExecutionsAsync(string tenantId,
        RtEntityId pipelineRtEntityId, DateTime? from, DateTime? to, int skip, int take);

    /// <summary>
    /// Gets all running executions for a specific adapter
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="adapterRtEntityId">Adapter identifier</param>
    /// <returns>List of running executions</returns>
    Task<IReadOnlyList<RtPipelineExecution>> GetRunningExecutionsForAdapterAsync(string tenantId,
        RtEntityId adapterRtEntityId);

    /// <summary>
    /// Gets execution IDs that are in Interrupted state for an adapter
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="adapterRtEntityId">Adapter identifier</param>
    /// <returns>List of interrupted execution IDs</returns>
    Task<IReadOnlyList<string>> GetInterruptedExecutionIdsAsync(string tenantId, RtEntityId adapterRtEntityId);

    /// <summary>
    /// Deletes executions older than the specified date
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="olderThan">Delete executions older than this date</param>
    /// <returns>Number of deleted executions</returns>
    Task<int> DeleteOldExecutionsAsync(string tenantId, DateTime olderThan);

    /// <summary>
    /// Finds running executions older than the specified date and marks them as failed
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="olderThan">Timeout threshold - executions started before this date are considered stale</param>
    /// <returns>Number of timed out executions</returns>
    Task<int> TimeoutStaleExecutionsAsync(string tenantId, DateTime olderThan);

    #endregion

    #region Pipeline Statistics

    /// <summary>
    /// Gets or creates statistics for a specific pipeline
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="pipelineRtEntityId">Pipeline identifier</param>
    /// <returns>Statistics entity or null if not found</returns>
    Task<RtPipelineStatistics?> GetPipelineStatisticsAsync(string tenantId, RtEntityId pipelineRtEntityId);

    /// <summary>
    /// Creates or updates pipeline statistics
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="statistics">Statistics to upsert</param>
    /// <param name="pipelineRtEntityId">Pipeline identifier</param>
    Task UpsertPipelineStatisticsAsync(string tenantId, RtPipelineStatistics statistics,
        RtEntityId pipelineRtEntityId);

    /// <summary>
    /// Gets aggregated execution statistics for a pipeline within a time range
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="pipelineRtEntityId">Pipeline identifier</param>
    /// <param name="from">Start of time range</param>
    /// <param name="to">End of time range</param>
    /// <returns>Aggregated statistics</returns>
    Task<ExecutionAggregateResult> GetExecutionAggregateAsync(string tenantId,
        RtEntityId pipelineRtEntityId, DateTime from, DateTime to);

    #endregion

    /// <summary>
    /// Bulk updates pipeline executions (queries all by executionId IN filter, then applies updates in one transaction)
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="updates">List of updates to apply</param>
    /// <returns>Number of successfully updated executions</returns>
    Task<int> BulkUpdatePipelineExecutionsAsync(string tenantId,
        IReadOnlyList<PipelineExecutionUpdate> updates);

    #region Bulk Operations (for offline sync)

    /// <summary>
    /// Bulk inserts pipeline executions
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="executions">Executions to insert</param>
    /// <param name="pipelineRtEntityId">Pipeline identifier</param>
    /// <param name="adapterRtEntityId">Adapter identifier</param>
    Task BulkInsertPipelineExecutionsAsync(string tenantId, IEnumerable<RtPipelineExecution> executions,
        RtEntityId pipelineRtEntityId, RtEntityId adapterRtEntityId);

    /// <summary>
    /// Gets existing execution IDs from a list (for deduplication)
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="executionIds">List of execution IDs to check</param>
    /// <returns>Set of existing execution IDs</returns>
    Task<ISet<string>> GetExistingExecutionIdsAsync(string tenantId, IEnumerable<string> executionIds);

    /// <summary>
    /// Updates the last synced sequence number for an adapter
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="adapterRtEntityId">Adapter identifier</param>
    /// <param name="sequenceNumber">New sequence number</param>
    Task UpdateAdapterSyncSequenceNumberAsync(string tenantId, RtEntityId adapterRtEntityId, int sequenceNumber);

    /// <summary>
    /// Gets the last synced sequence number for an adapter
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="adapterRtEntityId">Adapter identifier</param>
    /// <returns>Last synced sequence number</returns>
    Task<int> GetAdapterSyncSequenceNumberAsync(string tenantId, RtEntityId adapterRtEntityId);

    #endregion

    #region Pipeline Queries for Statistics

    /// <summary>
    /// Gets all pipelines for a tenant
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <returns>List of all pipelines</returns>
    Task<IReadOnlyCollection<RtPipeline>> GetAllPipelinesAsync(string tenantId);

    #endregion
}