using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v3;
using MongoDB.Bson;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

internal class AdapterServiceException : Exception
{
    private AdapterServiceException()
    {
    }

    private AdapterServiceException(string message) : base(message)
    {
    }

    private AdapterServiceException(string message, Exception inner) : base(message, inner)
    {
    }
    
    internal static Exception CommonFailedSetAdapterDeploymentState(string tenantId, RtEntityId adapterRtEntityId, RtDeploymentStateEnum deploymentState, Exception exception)
    {
        return new AdapterServiceException($"[{tenantId}] Failed to set adapter '{adapterRtEntityId}' deployment state to '{deploymentState}'", exception);
    }
    internal static Exception CommonFailedSetAdapterCommunicationState(string tenantId, RtEntityId adapterRtEntityId, RtCommunicationStateEnum communicationState, Exception exception)
    {
        return new AdapterServiceException($"[{tenantId}] Failed to set adapter '{adapterRtEntityId}' communication state to '{communicationState}'", exception);
    }

    internal static Exception CommonFailedCannotLoadAdapterConfiguration(string tenantId, RtEntityId adapterRtEntityId, Exception exception)
    {
        return new AdapterServiceException($"[{tenantId}] Failed to load adapter '{adapterRtEntityId}' configuration", exception);
    }

    internal static Exception AdapterNotLoaded(string tenantId, RtEntityId adapterRtEntityId)
    {
        return new AdapterServiceException(
            $"[{tenantId}] Adapter '{adapterRtEntityId}' has no live SignalR connection. " +
            "The adapter pod must be deployed and online before its pipeline configuration can be pushed. " +
            "Deploy the adapter first via the 'Deploy Adapter' action (or 'Pool → Deploy Workload' on the API), " +
            "then retry 'Update Configuration'.");
    }

    internal static Exception TenantNotEnabled(string tenantId)
    {
        return new AdapterServiceException($"[{tenantId}] Tenant not enabled.");
    }

    internal static Exception PipelineNotFound(string tenantId, RtEntityId pipelineRtEntityId)
    {
        return new AdapterServiceException($"[{tenantId}] Pipeline '{pipelineRtEntityId}' not found."); 
    }

    public static Exception DataFlowNotFound(string tenantId, RtEntityId rtEntityId)
    {
        return new AdapterServiceException($"[{tenantId}] Data flow of pipeline '{rtEntityId}' not found.");
    }

    public static Exception PreUpdateTenantFailed(string tenantId, Exception exception)
    {
        return new AdapterServiceException($"[{tenantId}] Pre update tenant failed.", exception);
    }
    
    public static Exception PosUpdateTenantFailed(string tenantId, Exception exception)
    {
        return new AdapterServiceException($"[{tenantId}] Pos update tenant failed.", exception);
    }

    public static Exception CkModelChangedNotificationFailed(string tenantId, Exception exception)
    {
        return new AdapterServiceException($"[{tenantId}] CK model change notification to adapters failed.", exception);
    }

    public static Exception CkTypeIdUndefined()
    {
        return new AdapterServiceException("CkTypeId is undefined.");
    }

    public static Exception RtWellKnownNameUndefined()
    {
        return new AdapterServiceException("RtWellKnownName is undefined.");
    }

    public static Exception DeploymentStateNotSupported(RtDeploymentStateEnum rDeploymentState)
    {
        return new AdapterServiceException($"Deployment state '{rDeploymentState}' is not supported.");
    }
    
    public static Exception DataFlowHasNoPipelines(string tenantId, OctoObjectId dataFlowRtId)
    {
        return new AdapterServiceException($"[{tenantId}] Data flow '{dataFlowRtId}' has no pipelines assigned.");
    }

    public static Exception PipelineAdapterNotAssigned(string tenantId, RtEntityId pipelineRtEntityId)
    {
        return new AdapterServiceException($"[{tenantId}] Pipeline '{pipelineRtEntityId}' has no adapter assigned. An adapter must be assigned when a pipeline exists.");
    }

    internal static AdapterServiceException DeploymentTimedOut(string tenantId, RtEntityId adapterRtEntityId,
        TimeSpan timeout) =>
        new($"[{tenantId}] Adapter '{adapterRtEntityId}' deployment timed out after {timeout.TotalSeconds}s");

    internal static AdapterServiceException DeploymentFailed(string tenantId, RtEntityId adapterRtEntityId,
        string? message) =>
        new($"[{tenantId}] Adapter '{adapterRtEntityId}' deployment failed: {message}");

    internal static AdapterServiceException PipelineSchemaValidationFailed(string tenantId,
        RtEntityId adapterRtEntityId, IReadOnlyList<string> errors) =>
        new($"[{tenantId}] Pipeline schema validation failed for adapter '{adapterRtEntityId}': {string.Join("; ", errors)}");
}
