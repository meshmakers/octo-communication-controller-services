using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services;

/// <summary>
///     AB#5279 — the expiry read is a courtesy to the member (no token is better than a dead one),
///     not a validation. Anything unreadable therefore counts as "not expired".
/// </summary>
internal class JwtExpiryTests
{
    private static string Jwt(string payloadJson)
    {
        var payload = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(payloadJson))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"eyJhbGciOiJSUzI1NiJ9.{payload}.c2ln";
    }

    [Test]
    public async Task AnExpClaimInThePastIsExpired()
    {
        var token = Jwt($"{{\"exp\":{DateTimeOffset.UtcNow.AddMinutes(-2).ToUnixTimeSeconds()}}}");
        await Assert.That(JwtExpiry.IsExpired(token, DateTime.UtcNow)).IsTrue();
    }

    [Test]
    public async Task AnExpClaimInTheFutureIsNotExpired()
    {
        var token = Jwt($"{{\"exp\":{DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds()},\"sub\":\"x\"}}");
        await Assert.That(JwtExpiry.IsExpired(token, DateTime.UtcNow)).IsFalse();
    }

    [Test]
    public async Task ExpiryWithinTheSkewCountsAsExpired()
    {
        // 10 s left is inside the 30 s skew window: a token the member would use a second later is
        // not worth carrying.
        var token = Jwt($"{{\"exp\":{DateTimeOffset.UtcNow.AddSeconds(10).ToUnixTimeSeconds()}}}");
        await Assert.That(JwtExpiry.IsExpired(token, DateTime.UtcNow)).IsTrue();
    }

    [Test]
    [Arguments("opaque-reference-token")]
    [Arguments("a.b")]
    [Arguments("eyJhbGciOiJSUzI1NiJ9.!!!notbase64!!!.c2ln")]
    public async Task AnythingThatIsNotAReadableJwtIsNotExpired(string token)
    {
        await Assert.That(JwtExpiry.IsExpired(token, DateTime.UtcNow)).IsFalse();
    }

    [Test]
    public async Task AJwtWithoutExpIsNotExpired()
    {
        await Assert.That(JwtExpiry.IsExpired(Jwt("{\"sub\":\"x\"}"), DateTime.UtcNow)).IsFalse();
    }
}
