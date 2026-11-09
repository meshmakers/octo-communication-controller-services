using System.Text.Json.Serialization;
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
    ///     Writes every valid SecretDefault of the Application into its target tenants and removes the defaults the
    ///     Application projected earlier but no longer provides to a tenant. Invalid entries, skips and failures of
    ///     single targets are stored as events in the source tenant; the method throws only on cancellation. An
    ///     existing default from an ancestor of the source tenant is kept: the source closer to the root wins.
    /// </summary>
    /// <param name="sourceTenantId">Tenant of the Application.</param>
    /// <param name="applicationRtId">RtId of the Application.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<SecretDefaultProvisioningResult> ProvisionForSourceAsync(string sourceTenantId,
        OctoObjectId applicationRtId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Removes every SecretDefault the Application projected into its own tenant and its subtree, as a redeploy
    ///     without entries would; where something was removed the tenant is reconciled so another source can take
    ///     the key over. Called when the Application is undeployed: only a deployed Application is a source.
    /// </summary>
    /// <param name="sourceTenantId">Tenant of the Application.</param>
    /// <param name="applicationRtId">RtId of the Application.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<SecretDefaultProvisioningResult> WithdrawForSourceAsync(string sourceTenantId,
        OctoObjectId applicationRtId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Brings the SecretDefaults of one tenant to the desired state: the defaults the deployed Applications of the
    ///     tenant and of all its ancestors provide to it are written (the source closer to the root wins), defaults no
    ///     Application provides any more are deleted. A source tenant that cannot be read keeps the defaults it
    ///     projected and is named in a Warning event; a tenant tree that cannot be read completely changes nothing.
    /// </summary>
    /// <param name="targetTenantId">Tenant whose defaults are reconciled.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<SecretDefaultTargetResult> ProvisionForTargetAsync(string targetTenantId,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Runs <see cref="ProvisionForTargetAsync" /> for the tenant and every tenant of its subtree.
    /// </summary>
    /// <param name="tenantId">Root of the reconciled subtree.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>One result per tenant, the given tenant first.</returns>
    Task<IReadOnlyList<SecretDefaultTargetResult>> ReconcileTenantAndSubtreeAsync(string tenantId,
        CancellationToken cancellationToken = default);
}

/// <summary>Counts of one projection run.</summary>
/// <param name="DefaultsWritten">SecretDefault entities created or overwritten.</param>
/// <param name="TargetsSkipped">Target tenants skipped: unavailable, without the model, or keeping an ancestor's default.</param>
/// <param name="TargetsFailed">Writes that failed, plus entries that are invalid or whose value could not be read.</param>
/// <param name="DefaultsRemoved">SecretDefault entities of the Application deleted because it no longer provides them.</param>
public sealed record SecretDefaultProvisioningResult(
    int DefaultsWritten,
    int TargetsSkipped,
    int TargetsFailed,
    int DefaultsRemoved = 0)
{
    /// <summary>A run that did nothing.</summary>
    public static readonly SecretDefaultProvisioningResult Nothing = new(0, 0, 0);
}

/// <summary>How the reconcile of one target tenant ended.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SecretDefaultTargetStatus>))]
public enum SecretDefaultTargetStatus
{
    /// <summary>The tenant was reconciled; single failures are counted in the result.</summary>
    Reconciled,

    /// <summary>The tenant cannot be resolved; nothing was changed.</summary>
    TenantUnavailable,

    /// <summary>The tenant lacks the System.Communication version that defines SecretDefault; nothing was changed.</summary>
    ModelMissing,

    /// <summary>The tenant tree could not be read completely; the existing defaults were kept.</summary>
    TreeUnreadable,

    /// <summary>The reconcile of the tenant failed as a whole (logged); what it changed before the failure stays.</summary>
    Failed
}

/// <summary>Counts of the reconcile of one target tenant; never a value.</summary>
/// <param name="TenantId">The reconciled tenant.</param>
/// <param name="Status">How the reconcile ended.</param>
/// <param name="DefaultsCreated">SecretDefault entities the tenant did not hold before.</param>
/// <param name="DefaultsUpdated">Existing SecretDefault entities overwritten because their source, name or value differed (restore, rotation, takeover).</param>
/// <param name="DefaultsRemoved">SecretDefault entities deleted because no Application provides them any more.</param>
/// <param name="DefaultsKept">Defaults left as they are: their source could not be read, or a source closer to the root holds the key.</param>
/// <param name="SourcesUnreadable">Source tenants (the tenant itself or an ancestor) whose Applications could not be read.</param>
/// <param name="WritesFailed">Writes or deletes that failed, plus values that could not be read.</param>
/// <param name="DefaultsUnchanged">Winning defaults the tenant already held as projected (source, name and value); not written.</param>
public sealed record SecretDefaultTargetResult(
    string TenantId,
    SecretDefaultTargetStatus Status,
    int DefaultsCreated,
    int DefaultsUpdated,
    int DefaultsRemoved,
    int DefaultsKept,
    int SourcesUnreadable,
    int WritesFailed,
    int DefaultsUnchanged = 0)
{
    /// <summary>SecretDefault entities created or overwritten.</summary>
    public int DefaultsWritten => DefaultsCreated + DefaultsUpdated;

    /// <summary>A result that changed nothing in the tenant.</summary>
    public static SecretDefaultTargetResult Unchanged(string tenantId, SecretDefaultTargetStatus status)
    {
        return new SecretDefaultTargetResult(tenantId, status, 0, 0, 0, 0, 0, 0);
    }
}
