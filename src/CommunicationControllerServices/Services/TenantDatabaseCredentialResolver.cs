using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Microsoft.Extensions.Options;
using NLog;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

/// <inheritdoc cref="ITenantDatabaseCredentialResolver" />
/// <remarks>
///     <para>
///         Two inputs, and the split between them is the whole point. The <b>tenant record</b> supplies
///         the database name — the controller is the only party that can read it for a borrower,
///         because on a pool member that read is itself gated by a credential the member does not have
///         until the lease installs one. The <b>installation's own configuration</b> supplies the user
///         naming rule and the password.
///     </para>
///     <para>
///         🔴 <b>Stream data (CrateDB) is not resolved here, and that is a decision rather than an
///         omission.</b> There is no per-tenant CrateDB principal to resolve: the engine holds one
///         <c>StreamDataConfiguration.ConnectionString</c> for the whole installation and separates
///         tenants by <i>schema</i>, not by credential (<c>CrateDbConnectionAccess</c> builds one
///         datasource per tenant from that single string). Putting it on the lease would therefore ship
///         every borrower the same installation-wide credential — time-scoped to the lease, but not
///         tenant-scoped — which looks like the mechanism above and is none of it, and would make the
///         operator's refusal of <c>secrets.streamDataPassword</c> decorative in exactly the way this
///         work item removed for Mongo. Until a per-tenant CrateDB user exists (the sibling of AB#5255
///         on the stream-data side), a leased pipeline that touches an archive fails to connect rather
///         than reaching another tenant's schema, which is the correct direction for that failure. The
///         seam is shaped so that adding it later touches this class, the two lease fields' stream-data
///         counterparts and one member-side participant — nothing else.
///     </para>
/// </remarks>
internal sealed class TenantDatabaseCredentialResolver(
    ISystemContext systemContext,
    IOptions<OctoSystemConfiguration> systemConfiguration) : ITenantDatabaseCredentialResolver
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    /// <inheritdoc />
    public async Task<TenantDatabaseCredential?> TryResolveAsync(string tenantId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        var configuration = systemConfiguration.Value;

        var userTemplate = configuration.DatabaseUser;
        if (string.IsNullOrWhiteSpace(userTemplate))
        {
            Logger.Warn(
                "[{TenantId}] This installation configures no datasource user name; no adapter-pool member " +
                "can be given access to a tenant database", tenantId);
            return null;
        }

        // 🔴 AB#5255 reads exactly this line, and nothing else.
        //
        // TODAY THIS PASSWORD IS INSTALLATION-WIDE. OctoSystemConfiguration.DatabaseUserPassword is a
        // single value behind every per-database user, so the credential a lease carries is per-tenant
        // in its AUTHORISATION (TenantContext.CreateTenantInternalAsync grants the user readWrite on
        // that one database and nothing else) and shared in its SECRET. That is a real and deliberate
        // residue, stated here rather than papered over: a member holding this password could, if it
        // also knew another tenant's database name and user, open that tenant's database. What stops
        // it is that it is told neither — the lease names one database and the member installs the
        // credential for that one alone.
        //
        // AB#5255 gives each database its own password. When it lands, only the right-hand side of
        // this assignment changes; the wire already carries user and password as two independent
        // fields, and the member already derives nothing.
        var password = configuration.DatabaseUserPassword;
        if (string.IsNullOrWhiteSpace(password))
        {
            Logger.Warn(
                "[{TenantId}] This controller holds no datasource password, so it cannot give an adapter-pool " +
                "member access to the tenant's database", tenantId);
            return null;
        }

        string? databaseName;
        try
        {
            var tenantContext = await systemContext.TryFindTenantContextAsync(tenantId).ConfigureAwait(false);
            databaseName = tenantContext?.DatabaseName;
        }
        catch (Exception e)
        {
            Logger.Warn(e, "[{TenantId}] Could not resolve the tenant's database name for a lease", tenantId);
            return null;
        }

        if (string.IsNullOrWhiteSpace(databaseName))
        {
            Logger.Warn("[{TenantId}] The tenant has no database record; no lease can be granted for it",
                tenantId);
            return null;
        }

        // The name is taken verbatim from the tenant record — the same string the record held when
        // CreateTenantInternalAsync formatted the user and granted it readWrite, and the same string
        // the member's engine will pass when it builds the connection. Re-normalising it here would
        // invent a third spelling of a value that already has exactly one authority.
        return new TenantDatabaseCredential(databaseName,
            string.Format(userTemplate, databaseName), password);
    }
}
