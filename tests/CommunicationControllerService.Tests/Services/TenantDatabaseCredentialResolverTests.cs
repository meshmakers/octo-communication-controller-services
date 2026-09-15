using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services;

/// <summary>
///     AB#4924 — resolving the database credential of a borrowing tenant.
/// </summary>
/// <remarks>
///     🔴 <b>This is the class AB#5255 will change, and these are the tests that say what it may not
///     change with it.</b> The user is per database and comes from the tenant record; the password is
///     a separate value; neither is computed from the other. When the follow-up gives each database its
///     own password, only the password assignment moves — if a test here has to be rewritten at the
///     same time, the seam did not hold.
/// </remarks>
internal class TenantDatabaseCredentialResolverTests
{
    private const string TenantId = "borrower";
    private const string DatabaseName = "borrowerdb";
    private const string Password = "dbPwd-Qv7Xr2Mn8Kt4Ws0Yh3Bd6Lp9Cf1Zg5Ja";

    private readonly ISystemContext _systemContext = Substitute.For<ISystemContext>();

    private readonly OctoSystemConfiguration _configuration = new()
    {
        DatabaseUser = "octo-system-ds-user-{0}",
        DatabaseUserPassword = Password
    };

    private ITenantDatabaseCredentialResolver Resolver =>
        new TenantDatabaseCredentialResolver(_systemContext, Options.Create(_configuration));

    private void ArrangeTenant(string databaseName = DatabaseName)
    {
        var tenantContext = Substitute.For<ITenantContext>();
        tenantContext.DatabaseName.Returns(databaseName);
        _systemContext.TryFindTenantContextAsync(TenantId).Returns(tenantContext);
    }

    /// <summary>
    ///     🔴 The user is the one the engine created for <b>that</b> database — the format applied to
    ///     the database name out of the tenant record, which is the same string
    ///     <c>TenantContext.CreateTenantInternalAsync</c> granted <c>readWrite</c> to. A resolver that
    ///     produced any other spelling would hand the member a user that does not exist.
    /// </summary>
    [Test]
    public async Task TheUserIsTheTenantsOwnDatasourceUser()
    {
        ArrangeTenant();

        var credential = await Resolver.TryResolveAsync(TenantId);

        using var _ = Assert.Multiple();
        await Assert.That(credential).IsNotNull();
        await Assert.That(credential!.Value.DatabaseName).IsEqualTo(DatabaseName);
        await Assert.That(credential.Value.User).IsEqualTo("octo-system-ds-user-borrowerdb");
        await Assert.That(credential.Value.Password).IsEqualTo(Password);
    }

    /// <summary>
    ///     Two tenants get two different users out of the same installation. The authorisation is
    ///     already per tenant today — only the secret is shared — and that is precisely why the user
    ///     has to travel as its own field.
    /// </summary>
    [Test]
    public async Task TwoTenantsResolveToTwoDifferentUsers()
    {
        ArrangeTenant();
        var otherContext = Substitute.For<ITenantContext>();
        otherContext.DatabaseName.Returns("neighbourdb");
        _systemContext.TryFindTenantContextAsync("neighbour").Returns(otherContext);

        var mine = await Resolver.TryResolveAsync(TenantId);
        var theirs = await Resolver.TryResolveAsync("neighbour");

        using var _ = Assert.Multiple();
        await Assert.That(mine!.Value.User).IsEqualTo("octo-system-ds-user-borrowerdb");
        await Assert.That(theirs!.Value.User).IsEqualTo("octo-system-ds-user-neighbourdb");
        await Assert.That(mine.Value.User).IsNotEqualTo(theirs.Value.User);
    }

    /// <summary>
    ///     A tenant with no record resolves to nothing. The caller turns that into a refusal; inventing
    ///     a database name from the tenant id would point a live credential at whatever database
    ///     happens to carry that name.
    /// </summary>
    [Test]
    public async Task AnUnknownTenantResolvesToNothing()
    {
        _systemContext.TryFindTenantContextAsync(TenantId).Returns((ITenantContext?)null);

        await Assert.That(await Resolver.TryResolveAsync(TenantId)).IsNull();
    }

    /// <summary>
    ///     🔴 A controller without a datasource password resolves to nothing rather than to a
    ///     credential with a blank one. A blank password would reach the member, which would then open
    ///     the connection unauthenticated and — on a host that happens to allow it — read the tenant's
    ///     data anyway, with no lease-scoped credential involved at all.
    /// </summary>
    [Test]
    public async Task AControllerWithoutADatasourcePasswordResolvesToNothing()
    {
        ArrangeTenant();
        _configuration.DatabaseUserPassword = null;

        await Assert.That(await Resolver.TryResolveAsync(TenantId)).IsNull();
    }

    /// <summary>
    ///     A tenant record with no database name is the same refusal. It is the shape a half-written
    ///     record takes, and it must not become <c>octo-system-ds-user-</c>.
    /// </summary>
    [Test]
    public async Task ATenantRecordWithoutADatabaseNameResolvesToNothing()
    {
        ArrangeTenant(string.Empty);

        await Assert.That(await Resolver.TryResolveAsync(TenantId)).IsNull();
    }

    /// <summary>
    ///     A registry read that throws is a refusal too, not an exception out of a hub method — the
    ///     caller is on the grant path of a SignalR hub.
    /// </summary>
    [Test]
    public async Task ARegistryReadThatThrowsResolvesToNothing()
    {
        _systemContext.TryFindTenantContextAsync(TenantId)
            .Returns<Task<ITenantContext?>>(_ => throw new InvalidOperationException("registry down"));

        await Assert.That(await Resolver.TryResolveAsync(TenantId)).IsNull();
    }

    /// <summary>
    ///     🔴 The credential's own rendering never prints the password. This type is one interpolation
    ///     away from a refusal message, and a positional record's generated <c>ToString</c> prints
    ///     every member.
    /// </summary>
    [Test]
    public async Task TheCredentialNeverRendersItsPassword()
    {
        var rendered = new TenantDatabaseCredential(DatabaseName, "octo-system-ds-user-borrowerdb", Password)
            .ToString();

        using var _ = Assert.Multiple();
        await Assert.That(rendered).DoesNotContain(Password);
        await Assert.That(rendered).DoesNotContain(Password[..8]);
        await Assert.That(rendered).Contains("octo-system-ds-user-borrowerdb");
    }
}
