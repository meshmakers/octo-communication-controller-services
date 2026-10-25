using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Communication.Contracts.MessageObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.ExecutionEnvelopeTests;

/// <summary>
///     AB#7472 — the generic sealed envelope (reused for the method-invocation marker of AB#7461).
/// </summary>
internal class ExecutionEnvelopeTests
{
    private const string Tenant = "acme";
    private const string Purpose = "test-purpose";

    private readonly ExecutionEnvelope _sut = TestExecutionEnvelope.Create();
    private readonly OctoObjectId _execution = OctoObjectId.GenerateNewId();

    private sealed record Data(string Name, int Level);

    [Test]
    public async Task SealThenOpen_RoundTripsTheData()
    {
        var stored = _sut.Seal(Purpose, new Data("x", 2), Tenant, _execution);

        var ok = _sut.TryOpen<Data>(Purpose, stored, Tenant, _execution, out var data, out var why);

        using var scope = Assert.Multiple();
        await Assert.That(ok).IsTrue();
        await Assert.That(why).IsEqualTo(ExecutionEnvelopeRejection.None);
        await Assert.That(data).IsEqualTo(new Data("x", 2));
    }

    [Test]
    public async Task TheStoredValue_IsAnEncV2Envelope_WithoutThePlaintext()
    {
        var stored = _sut.Seal(Purpose, new Data("a-very-recognisable-name", 2), Tenant, _execution)!;

        using var scope = Assert.Multiple();
        await Assert.That(stored).StartsWith("enc:v2:");
        await Assert.That(stored.Contains("a-very-recognisable-name")).IsFalse();
    }

    [Test]
    public async Task TwoSeals_OfTheSameData_Differ()
    {
        var a = _sut.Seal(Purpose, new Data("x", 1), Tenant, _execution);
        var b = _sut.Seal(Purpose, new Data("x", 1), Tenant, _execution);

        await Assert.That(a).IsNotEqualTo(b);
    }

    [Test]
    [Arguments("other-purpose", Tenant, false, ExecutionEnvelopeRejection.WrongPurpose)]
    [Arguments(Purpose, "other-tenant", false, ExecutionEnvelopeRejection.WrongTenant)]
    [Arguments(Purpose, Tenant, true, ExecutionEnvelopeRejection.WrongExecution)]
    public async Task TheBinding_IsPartOfTheAuthenticatedContent(string purpose, string tenant,
        bool otherExecution, ExecutionEnvelopeRejection expected)
    {
        var stored = _sut.Seal(Purpose, new Data("x", 1), Tenant, _execution);

        var ok = _sut.TryOpen<Data>(purpose, stored, tenant, otherExecution ? OctoObjectId.GenerateNewId() : _execution,
            out var data, out var why);

        using var scope = Assert.Multiple();
        await Assert.That(ok).IsFalse();
        await Assert.That(why).IsEqualTo(expected);
        await Assert.That(data).IsNull();
    }

    [Test]
    [Arguments(null, ExecutionEnvelopeRejection.Missing)]
    [Arguments("", ExecutionEnvelopeRejection.Missing)]
    [Arguments("plain text", ExecutionEnvelopeRejection.NotAnEnvelope)]
    [Arguments("{\"v\":1}", ExecutionEnvelopeRejection.NotAnEnvelope)]
    [Arguments("enc:v1:AAAA", ExecutionEnvelopeRejection.NotAnEnvelope)]
    [Arguments("enc:v2:garbage", ExecutionEnvelopeRejection.NotAnEnvelope)]
    public async Task ForeignValues_DoNotOpen(string? stored, ExecutionEnvelopeRejection expected)
    {
        var ok = _sut.TryOpen<Data>(Purpose, stored, Tenant, _execution, out _, out var why);

        using var scope = Assert.Multiple();
        await Assert.That(ok).IsFalse();
        await Assert.That(why).IsEqualTo(expected);
    }

    [Test]
    public async Task ATamperedCiphertext_DoesNotOpen()
    {
        var stored = _sut.Seal(Purpose, new Data("x", 1), Tenant, _execution)!;
        var chars = stored.ToCharArray();
        chars[^3] = chars[^3] == 'A' ? 'B' : 'A';

        var ok = _sut.TryOpen<Data>(Purpose, new string(chars), Tenant, _execution, out _, out var why);

        using var scope = Assert.Multiple();
        await Assert.That(ok).IsFalse();
        await Assert.That(why).IsEqualTo(ExecutionEnvelopeRejection.Unreadable);
    }

    [Test]
    public async Task AnotherSecretOfTheSameKeyRing_IsNoEnvelope()
    {
        // not a decryption oracle: whatever it decrypts to is only compared, never returned
        var keys = TestSecretEncryption.Create();
        var sut = new ExecutionEnvelope(keys.Protector);
        var copiedSecret = keys.Protector.Protect("a-client-secret").Envelope;

        var ok = sut.TryOpen<Data>(Purpose, copiedSecret, Tenant, _execution, out _, out var why);

        using var scope = Assert.Multiple();
        await Assert.That(ok).IsFalse();
        await Assert.That(why).IsEqualTo(ExecutionEnvelopeRejection.Malformed);
    }

    [Test]
    public async Task AValueOfAnotherKeyRing_DoesNotOpen()
    {
        var stored = TestExecutionEnvelope.Create().Seal(Purpose, new Data("x", 1), Tenant, _execution);

        var ok = _sut.TryOpen<Data>(Purpose, stored, Tenant, _execution, out _, out _);

        await Assert.That(ok).IsFalse();
    }

    [Test]
    public async Task WithoutAKeyRing_SealStoresNothing_AndNothingOpens()
    {
        var sealed1 = _sut.Seal(Purpose, new Data("x", 1), Tenant, _execution);
        var noKeys = TestExecutionEnvelope.Create(withKeyRing: false);

        var stored = noKeys.Seal(Purpose, new Data("x", 1), Tenant, _execution);
        var ok = noKeys.TryOpen<Data>(Purpose, sealed1, Tenant, _execution, out _, out _);

        using var scope = Assert.Multiple();
        await Assert.That(stored).IsNull();
        await Assert.That(ok).IsFalse();
    }

    [Test]
    public async Task Reseal_BindsTheDataToTheNewExecution()
    {
        var next = OctoObjectId.GenerateNewId();
        var stored = _sut.Seal(Purpose, new Data("x", 3), Tenant, _execution);

        var resealed = _sut.Reseal<Data>(Purpose, stored, Tenant, _execution, next);

        using var scope = Assert.Multiple();
        await Assert.That(resealed).IsNotNull();
        await Assert.That(resealed).IsNotEqualTo(stored);
        await Assert.That(_sut.TryOpen<Data>(Purpose, resealed, Tenant, next, out var data, out _)).IsTrue();
        await Assert.That(data).IsEqualTo(new Data("x", 3));
        await Assert.That(_sut.TryOpen<Data>(Purpose, resealed, Tenant, _execution, out _, out _)).IsFalse();
    }

    [Test]
    public async Task Reseal_RefusesAValueThatDoesNotVerifyForTheOldExecution()
    {
        var stored = _sut.Seal(Purpose, new Data("x", 3), Tenant, OctoObjectId.GenerateNewId());

        await Assert.That(_sut.Reseal<Data>(Purpose, stored, Tenant, _execution, OctoObjectId.GenerateNewId()))
            .IsNull();
    }

    // ---- QueuedCaller -------------------------------------------------------------------------

    [Test]
    public async Task StripToken_KeepsTheCallerButDropsTheToken_ForTheSameExecution()
    {
        var execution = new RtPipelineExecution { RtId = _execution };
        QueuedCaller.Apply(execution, Tenant,
            new ExecutePipelineCaller { SubjectId = "user-42", Roles = ["Admin"], TrustLevel = 2 }, "bearer-xyz", _sut);

        var stripped = QueuedCaller.StripToken(execution.SealedCaller, Tenant, _execution, _sut);
        var opened = QueuedCaller.Open(stripped, Tenant, _execution, _sut);

        using var scope = Assert.Multiple();
        await Assert.That(opened).IsNotNull();
        await Assert.That(opened!.Caller.SubjectId).IsEqualTo("user-42");
        await Assert.That(opened.Caller.TrustLevel).IsEqualTo(2);
        await Assert.That(opened.AccessToken).IsNull();
    }

    [Test]
    public async Task QueuedCallerReseal_NeverCarriesTheToken_EvenFromAPreClaimValue()
    {
        var next = OctoObjectId.GenerateNewId();
        var execution = new RtPipelineExecution { RtId = _execution };
        QueuedCaller.Apply(execution, Tenant, new ExecutePipelineCaller { SubjectId = "user-42", TrustLevel = 2 },
            "bearer-xyz", _sut);

        var resealed = QueuedCaller.Reseal(execution.SealedCaller, Tenant, _execution, next, _sut);
        var opened = QueuedCaller.Open(resealed, Tenant, next, _sut);

        using var scope = Assert.Multiple();
        await Assert.That(opened).IsNotNull();
        await Assert.That(opened!.Caller.SubjectId).IsEqualTo("user-42");
        await Assert.That(opened.AccessToken).IsNull();
    }

    [Test]
    public async Task AnEnvelopeWithoutData_IsMalformed_AndDoesNotThrow()
    {
        // Same key ring, correct binding, but no "d" member: only a holder of the key could produce it, still it
        // must be refused rather than throw.
        var keys = TestSecretEncryption.Create();
        var sut = new ExecutionEnvelope(keys.Protector);
        var json = "{\"v\":1,\"p\":\"" + Purpose + "\",\"t\":\"" + Tenant + "\",\"e\":\"" + _execution + "\"}";
        var stored = keys.Protector.Protect(json).Envelope;

        var ok = sut.TryOpen<Data>(Purpose, stored, Tenant, _execution, out _, out var why);

        using var scope = Assert.Multiple();
        await Assert.That(ok).IsFalse();
        await Assert.That(why).IsEqualTo(ExecutionEnvelopeRejection.Malformed);
    }

    [Test]
    public async Task StripToken_OfAValueThatDoesNotVerify_ClearsTheAttribute()
    {
        await Assert.That(QueuedCaller.StripToken("user-42", Tenant, _execution, _sut)).IsEqualTo(string.Empty);
    }
}
