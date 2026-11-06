using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;

/// <summary>Whether the SecretDefaults of a tenant could be read.</summary>
public enum SecretDefaultTenantAvailability
{
    /// <summary>The tenant was read; <see cref="SecretDefaultInventory.Defaults" /> is complete.</summary>
    Available,

    /// <summary>The tenant cannot be resolved.</summary>
    TenantUnavailable,

    /// <summary>The tenant lacks the System.Communication version that defines SecretDefault.</summary>
    ModelMissing
}

/// <summary>
///     A stored SecretDefault without its value: what the reconcile needs to decide whether the entity is still
///     provided by an Application.
/// </summary>
/// <param name="RtId">RtId of the SecretDefault entity.</param>
/// <param name="Key">Well-known name of the entity, the key the consumer reads.</param>
/// <param name="SourceTenantId">Tenant of the Application that projected it, when set.</param>
/// <param name="SourceWorkloadRtId">RtId of that Application, when set.</param>
public sealed record StoredSecretDefault(
    OctoObjectId RtId,
    string Key,
    string? SourceTenantId,
    string? SourceWorkloadRtId);

/// <summary>The SecretDefaults of one tenant, never their values.</summary>
/// <param name="Availability">Whether the tenant could be read.</param>
/// <param name="Defaults">The stored defaults; empty unless <see cref="SecretDefaultTenantAvailability.Available" />.</param>
public sealed record SecretDefaultInventory(
    SecretDefaultTenantAvailability Availability,
    IReadOnlyList<StoredSecretDefault> Defaults)
{
    /// <summary>A tenant that cannot be resolved.</summary>
    public static readonly SecretDefaultInventory TenantUnavailable =
        new(SecretDefaultTenantAvailability.TenantUnavailable, []);

    /// <summary>A tenant without the System.Communication version that defines SecretDefault.</summary>
    public static readonly SecretDefaultInventory ModelMissing =
        new(SecretDefaultTenantAvailability.ModelMissing, []);
}
