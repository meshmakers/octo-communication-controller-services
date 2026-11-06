using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

/// <summary>
///     Projects the SecretDefaults an Application declares into the platform-owned SecretDefault entity of
///     every tenant the Application serves: its own tenant and its whole subtree, optionally narrowed by
///     AllowedTenantIds, never siblings.
/// </summary>
public interface ISecretDefaultProvisioningService
{
    /// <summary>
    ///     Writes every SecretDefault of the Application into its target tenants. Skips and failures of single
    ///     targets are stored as events in the source tenant; the method throws only on cancellation.
    /// </summary>
    /// <param name="sourceTenantId">Tenant of the Application.</param>
    /// <param name="applicationRtId">RtId of the Application.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<SecretDefaultProvisioningResult> ProvisionForSourceAsync(string sourceTenantId,
        OctoObjectId applicationRtId, CancellationToken cancellationToken = default);
}

/// <summary>Counts of one projection run.</summary>
/// <param name="DefaultsWritten">SecretDefault entities created or overwritten.</param>
/// <param name="TargetsSkipped">Target tenants skipped because they are unavailable or lack the model.</param>
/// <param name="TargetsFailed">Writes that failed, including defaults whose value could not be read.</param>
public sealed record SecretDefaultProvisioningResult(int DefaultsWritten, int TargetsSkipped, int TargetsFailed)
{
    /// <summary>A run that did nothing.</summary>
    public static readonly SecretDefaultProvisioningResult Nothing = new(0, 0, 0);
}
