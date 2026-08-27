using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v3;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

/// <summary>
/// A pool or workload of a tenant whose <c>DeploymentState</c> says it still owns operator-managed
/// resources (anything but <c>Undeployed</c> / <c>Disabled</c>). Produced by
/// <see cref="IPoolService.GetActiveDeploymentsAsync"/> for the Communication disable guard (AB#4255).
/// </summary>
/// <param name="Kind">"Pool", "Adapter" or "Application"</param>
/// <param name="Name">Entity name, falling back to the runtime id</param>
/// <param name="State">The persisted deployment state</param>
public sealed record ActiveDeployment(string Kind, string Name, RtDeploymentStateEnum State)
{
    /// <summary>Kind label of a pool</summary>
    public const string PoolKind = "Pool";

    /// <summary>Kind label of an adapter workload</summary>
    public const string AdapterKind = "Adapter";

    /// <summary>Kind label of an application workload</summary>
    public const string ApplicationKind = "Application";

    /// <summary>
    /// True for the states the deployment-state recompute never touches because they own real
    /// operator resources: Deployed, Pending (deploy in flight) and Error (a failed helm install may
    /// still hold a partial release). Undeployed and Disabled are the resting states.
    /// </summary>
    public static bool IsActive(RtDeploymentStateEnum state)
    {
        return state is not (RtDeploymentStateEnum.Undeployed or RtDeploymentStateEnum.Disabled);
    }

    /// <summary>Renders <c>Kind 'Name' (State)</c> — the fragment used in the operator message.</summary>
    public override string ToString()
    {
        return $"{Kind} '{Name}' ({State})";
    }
}
