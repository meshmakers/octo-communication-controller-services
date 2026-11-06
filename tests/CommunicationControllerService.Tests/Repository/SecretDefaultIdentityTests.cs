using System.Net;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Clusters;
using MongoDB.Driver.Core.Connections;
using MongoDB.Driver.Core.Servers;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Repository;

/// <summary>
///     A new SecretDefault gets an RtId derived from its key, so two writers creating the same key at the same time
///     collide on the document id instead of leaving two entities with one name.
/// </summary>
internal sealed class SecretDefaultIdentityTests
{
    [Test]
    public async Task TheSameKey_AlwaysYieldsTheSameRtId()
    {
        var firstRtId = SecretDefaultIdentity.RtIdFor("ec-captcha-secret");
        var secondRtId = SecretDefaultIdentity.RtIdFor("ec-captcha-secret");

        await Assert.That(secondRtId).IsEqualTo(firstRtId);
    }

    [Test]
    public async Task DifferentKeys_YieldDifferentRtIds()
    {
        var captchaRtId = SecretDefaultIdentity.RtIdFor("ec-captcha-secret");
        var otherRtId = SecretDefaultIdentity.RtIdFor("ec-captcha-secret2");

        await Assert.That(otherRtId).IsNotEqualTo(captchaRtId);
    }

    [Test]
    public async Task KeysDifferingOnlyInCase_YieldDifferentRtIds()
    {
        var lowerCaseRtId = SecretDefaultIdentity.RtIdFor("captcha");
        var upperCaseRtId = SecretDefaultIdentity.RtIdFor("Captcha");

        await Assert.That(upperCaseRtId).IsNotEqualTo(lowerCaseRtId);
    }

    [Test]
    public async Task AWriteConflict_IsACollisionToRetry()
    {
        var writeConflict = new MongoCommandException(ConnectionIdForTests(), "write conflict", new BsonDocument(),
            new BsonDocument { { "code", 112 }, { "codeName", "WriteConflict" } });

        await Assert.That(SecretDefaultIdentity.IsConcurrentCreateCollision(writeConflict)).IsTrue();
    }

    [Test]
    public async Task ADuplicateKeyOnTheDocumentId_IsACollisionToRetry()
    {
        var duplicateKey = new MongoCommandException(ConnectionIdForTests(), "duplicate key", new BsonDocument(),
            new BsonDocument { { "code", 11000 }, { "codeName", "DuplicateKey" } });

        await Assert.That(SecretDefaultIdentity.IsConcurrentCreateCollision(
            new InvalidOperationException("wrapped by the engine", duplicateKey))).IsTrue();
    }

    [Test]
    public async Task AnyOtherFailure_IsNotRetried()
    {
        await Assert.That(SecretDefaultIdentity.IsConcurrentCreateCollision(new InvalidOperationException("other")))
            .IsFalse();
    }

    private static ConnectionId ConnectionIdForTests()
    {
        return new ConnectionId(new ServerId(new ClusterId(1), new DnsEndPoint("localhost", 27017)));
    }
}
