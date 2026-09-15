namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

/// <summary>
///     The credential a pool member needs in order to open one tenant's database (AB#4924).
/// </summary>
/// <param name="DatabaseName">
///     The database the credential is valid for — the scope key, not a secret. The member installs the
///     credential for this database and for no other, because it also opens databases that are not the
///     borrower's.
/// </param>
/// <param name="User">The database user, resolved by the controller. Never derived on the member.</param>
/// <param name="Password">
///     🔴 That user's password, in plaintext. Travels on the lease and is subject to every rule the
///     borrower's client secret is: lease-scoped, never persisted, never logged.
/// </param>
public readonly record struct TenantDatabaseCredential(string DatabaseName, string User, string Password)
{
    /// <summary>
    ///     🔴 Deliberately does not render <see cref="Password" />. A positional record's generated
    ///     <c>ToString</c> prints every member, and this type exists one call away from a refusal
    ///     message.
    /// </summary>
    public override string ToString() => $"database '{DatabaseName}' as '{User}'";
}

/// <summary>
///     Resolves the database credential of a <b>borrowing</b> tenant (AB#4924, concept §4).
/// </summary>
/// <remarks>
///     <para>
///         🔴 <b>This interface exists so that AB#5255 is a one-line change.</b> Today the installation
///         has one datasource password behind every per-database user, so the resolver returns the same
///         password for every tenant while the <i>user</i> is already per tenant — the authorisation is
///         per tenant, only the secret is shared. AB#5255 gives each database its own password, and it
///         must be able to change <b>only where the password comes from</b>: not the wire
///         (<c>LeaseDto.DatabaseUser</c> / <c>DatabasePassword</c> are already two independent fields)
///         and not the member (which applies what it is given and derives nothing). Putting the
///         resolution behind this seam is what keeps that promise checkable.
///     </para>
///     <para>
///         <b>Stream data (CrateDB) is deliberately not here.</b> See the remarks on the
///         implementation for why, and what has to exist first.
///     </para>
/// </remarks>
public interface ITenantDatabaseCredentialResolver
{
    /// <summary>
    ///     The tenant's database credential, or <c>null</c> when it cannot be resolved.
    /// </summary>
    /// <remarks>
    ///     🔴 <b>Null is a refusal, not a default.</b> A caller that fell back to "no credential" would
    ///     hand a pool member a lease it can only serve with whatever credentials its own process
    ///     happens to hold — which is precisely the shared-credential failure this mechanism removes.
    /// </remarks>
    Task<TenantDatabaseCredential?> TryResolveAsync(string tenantId,
        CancellationToken cancellationToken = default);
}
