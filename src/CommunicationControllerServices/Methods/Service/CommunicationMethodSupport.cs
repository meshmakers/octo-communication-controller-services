using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using Meshmakers.Octo.Services.Methods;
using Meshmakers.Octo.Services.Methods.Authorization;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Methods.Service;

/// <summary>The error codes the System.Communication methods declare (AB#8595); the CK model is the contract.</summary>
internal static class CommunicationMethodErrors
{
    /// <summary><c>Adapter.DeployUpdate</c>: the configuration could not be pushed (REST 400).</summary>
    public const string AdapterConfigurationDeployFailed = "ADAPTER_CONFIGURATION_DEPLOY_FAILED";

    /// <summary><c>DeployableWorkload.Wake</c>: the wake failed or timed out (REST 400).</summary>
    public const string WorkloadWakeFailed = "WORKLOAD_WAKE_FAILED";

    /// <summary><c>Adapter.ReconcileServiceAccount</c>: the reconcile did not complete (REST 400).</summary>
    public const string ServiceAccountReconcileFailed = "SERVICE_ACCOUNT_RECONCILE_FAILED";

    /// <summary><c>Adapter.RotateServiceAccountSecret</c>: the rotation did not happen (REST 400).</summary>
    public const string ServiceAccountRotationFailed = "SERVICE_ACCOUNT_ROTATION_FAILED";

    /// <summary><c>AdapterPool.CancelQueuedExecution</c>: the execution already holds a lease (REST 409).</summary>
    public const string ExecutionAlreadyLeased = "EXECUTION_ALREADY_LEASED";

    /// <summary><c>AdapterPool.CancelQueuedExecution</c>: no such queued execution in the pool (REST 404).</summary>
    public const string QueuedExecutionNotFound = "QUEUED_EXECUTION_NOT_FOUND";

    /// <summary>Error parameter with the service's message, as the REST error body carries it. Never contains a secret.</summary>
    public const string MessageParameter = "message";
}

/// <summary>Steps the handlers share: the target id and the adapter lookup the REST endpoints use.</summary>
internal static class CommunicationMethodSupport
{
    /// <summary>The audit text of a change made through a method (the REST endpoints write <c>source: User</c>).</summary>
    public const string MethodSource = "source: User via CK method";

    /// <summary>The target as object id; <c>false</c> for a missing or malformed id (it cannot exist).</summary>
    public static bool TryGetTarget(CkMethodContext context, out OctoObjectId target)
    {
        target = default;
        return !string.IsNullOrWhiteSpace(context.TargetRtId) && OctoObjectId.TryParse(context.TargetRtId, out target);
    }

    /// <summary>
    ///     The adapter of the tenant with this id, resolved through the tenant's adapter list as the REST endpoints do
    ///     (Adapter is polymorphic: MeshAdapter, PlugAdapter, …); <c>null</c> when the tenant has none with this id.
    /// </summary>
    public static async Task<RtAdapter?> FindAdapterAsync(ICommunicationRepository repository, string tenantId,
        OctoObjectId adapterRtId)
    {
        var adapters = await repository.GetAdaptersAsync(tenantId).ConfigureAwait(false);
        return adapters.FirstOrDefault(a => a.RtId == adapterRtId);
    }

    /// <summary><c>true</c> when the validated caller holds the role (case-insensitive, as the token's role claims).</summary>
    public static bool HasRole(CkMethodPrincipal? principal, string role) =>
        principal?.Roles.Contains(role, StringComparer.OrdinalIgnoreCase) == true;
}
