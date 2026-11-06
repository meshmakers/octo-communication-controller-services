using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;

/// <summary>
///     One SecretDefault to write into a target tenant. Not a record on purpose: a generated
///     <c>ToString</c> would render the value.
/// </summary>
/// <param name="key">Well-known name and name of the SecretDefault entity.</param>
/// <param name="value">The value to store; the engine encrypts it on write.</param>
/// <param name="sourceTenantId">Tenant of the Application that declares the default.</param>
/// <param name="sourceWorkloadRtId">Bare RtId of that Application.</param>
/// <param name="projectedAt">UTC time of this projection.</param>
public sealed class SecretDefaultWrite(
    string key,
    RtSecretValue value,
    string sourceTenantId,
    string sourceWorkloadRtId,
    DateTime projectedAt)
{
    /// <summary>Well-known name and name of the SecretDefault entity.</summary>
    public string Key { get; } = key;

    /// <summary>The value to store; the engine encrypts it on write.</summary>
    public RtSecretValue Value { get; } = value;

    /// <summary>Tenant of the Application that declares the default.</summary>
    public string SourceTenantId { get; } = sourceTenantId;

    /// <summary>Bare RtId of the Application that declares the default.</summary>
    public string SourceWorkloadRtId { get; } = sourceWorkloadRtId;

    /// <summary>UTC time of this projection.</summary>
    public DateTime ProjectedAt { get; } = projectedAt;

    /// <inheritdoc />
    public override string ToString()
    {
        return $"SecretDefault '{Key}' from {SourceTenantId}/{SourceWorkloadRtId}";
    }
}

/// <summary>What a SecretDefault write did in one target tenant.</summary>
public enum SecretDefaultWriteOutcome
{
    /// <summary>The entity did not exist and was created.</summary>
    Created,

    /// <summary>The existing entity was overwritten.</summary>
    Updated,

    /// <summary>The target tenant cannot be resolved; nothing was written.</summary>
    TenantUnavailable,

    /// <summary>The target tenant lacks the System.Communication version that defines SecretDefault; nothing was written.</summary>
    ModelMissing
}
