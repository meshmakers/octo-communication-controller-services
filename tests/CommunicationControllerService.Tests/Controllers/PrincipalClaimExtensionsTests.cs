using System.Security.Claims;
using Duende.IdentityModel;
using Meshmakers.Octo.Backend.CommunicationControllerServices.TenantApi.v1.Controllers;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Controllers;

/// <summary>
///     AB#5328 — claim reads that survive the JWT handler's inbound claim mapping.
/// </summary>
/// <remarks>
///     <para>
///         The bearer pipeline runs with <c>MapInboundClaims</c> on, so a principal built from a real
///         token carries <see cref="ClaimTypes.NameIdentifier" /> / <see cref="ClaimTypes.Role" /> /
///         <see cref="ClaimTypes.Name" />, not <c>sub</c> / <c>role</c> / <c>name</c>. Code that asks
///         for the raw spelling gets nothing — and because every read in
///         <c>BuildExecutePipelineCaller</c> has a fallback, it got something plausible instead of an
///         error: measured on 23.09.2026 a caller with a subject and seventeen roles was persisted as
///         subject <c>octo-cli</c> (the OAuth client) with no roles at all.
///     </para>
///     <para>
///         🔴 The tests that should have caught it build principals with <c>new Claim("sub", …)</c> —
///         the shape the runtime never delivers. The mapped cases below are therefore the point of
///         this file; the raw ones only pin that the older shape keeps working.
///     </para>
/// </remarks>
internal class PrincipalClaimExtensionsTests
{
    private const string Subject = "6a645b02a77a0c28af778fa9";

    private static ClaimsPrincipal MappedPrincipal()
    {
        return new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, Subject),
            new Claim("client_id", "octo-cli"),
            new Claim(ClaimTypes.Name, "Gerald"),
            new Claim(ClaimTypes.Email, "gerald@example.test"),
            new Claim(ClaimTypes.Role, "CommunicationManagement"),
            new Claim(ClaimTypes.Role, "AccountingEmployee")
        ], "Bearer"));
    }

    private static ClaimsPrincipal RawPrincipal()
    {
        return new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(JwtClaimTypes.Subject, Subject),
            new Claim("client_id", "octo-cli"),
            new Claim(JwtClaimTypes.Name, "Gerald"),
            new Claim(JwtClaimTypes.Email, "gerald@example.test"),
            new Claim(JwtClaimTypes.Role, "CommunicationManagement")
        ], "Bearer"));
    }

    [Test]
    public async Task AMappedPrincipal_ReportsTheSubjectAndNotTheClientId()
    {
        await Assert.That(MappedPrincipal().SubjectId()).IsEqualTo(Subject);
    }

    [Test]
    public async Task AMappedPrincipal_ReportsItsRoles()
    {
        var roles = MappedPrincipal().RoleValues();

        using var _ = Assert.Multiple();
        await Assert.That(roles.Length).IsEqualTo(2);
        await Assert.That(roles).Contains("AccountingEmployee");
    }

    [Test]
    public async Task AMappedPrincipal_ReportsNameAndEmail()
    {
        var principal = MappedPrincipal();

        using var _ = Assert.Multiple();
        await Assert.That(principal.DisplayName()).IsEqualTo("Gerald");
        await Assert.That(principal.Email()).IsEqualTo("gerald@example.test");
    }

    [Test]
    public async Task ARawPrincipal_StillWorks()
    {
        var principal = RawPrincipal();

        using var _ = Assert.Multiple();
        await Assert.That(principal.SubjectId()).IsEqualTo(Subject);
        await Assert.That(principal.DisplayName()).IsEqualTo("Gerald");
        await Assert.That(principal.Email()).IsEqualTo("gerald@example.test");
        await Assert.That(principal.RoleValues().Length).IsEqualTo(1);
    }

    /// <summary>
    ///     A principal built from a mapped token can legitimately carry both spellings of the same
    ///     role. One role listed twice is not two roles.
    /// </summary>
    [Test]
    public async Task BothSpellingsOfOneRole_CountOnce()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Role, "CommunicationManagement"),
            new Claim(JwtClaimTypes.Role, "CommunicationManagement")
        ], "Bearer"));

        await Assert.That(principal.RoleValues().Length).IsEqualTo(1);
    }

    /// <summary>
    ///     A client-credentials token genuinely has no subject, and the OAuth client is then the
    ///     honest answer — but only as the LAST resort, never ahead of a real subject.
    /// </summary>
    [Test]
    public async Task WithoutASubject_TheClientIdIsTheFallback()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("client_id", "octo-cli")], "Bearer"));

        await Assert.That(principal.SubjectId()).IsEqualTo("octo-cli");
    }

    [Test]
    public async Task AnEmptyPrincipal_ReportsNothingRatherThanThrowing()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity());

        using var _ = Assert.Multiple();
        await Assert.That(principal.SubjectId()).IsNull();
        await Assert.That(principal.DisplayName()).IsNull();
        await Assert.That(principal.Email()).IsNull();
        await Assert.That(principal.RoleValues().Length).IsEqualTo(0);
    }
}
