using System.Security.Cryptography;
using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Options;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.ExecutionEnvelopeTests;

/// <summary>
///     A sealed execution value is bound to its purpose, tenant and execution by the associated data of the key ring,
///     not only by the content it carries. A value produced through a Secret attribute (whose envelope a tenant
///     administrator can read in a dump) therefore never opens as a sealed execution value, and a sealed execution
///     value never opens as the value of a Secret attribute. A value sealed in the former form opens only until
///     <see cref="CommunicationControllerOptions.SealedValueLegacyFormAcceptedUntilUtc" />.
/// </summary>
internal class ExecutionEnvelopePurposeBindingTests
{
    private const string Tenant = "acme";
    private const string Purpose = ExecutionEnvelopePurposes.QueuedCaller;

    private static readonly DateTimeOffset LegacyFormDeadline = new(2027, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly OctoObjectId _execution = OctoObjectId.GenerateNewId();
    private readonly TestSecretEncryption _keys = TestSecretEncryption.Create();

    private sealed record Data(string Name, int Level);

    private ExecutionEnvelope CreateEnvelope(DateTimeOffset now)
    {
        var options = Microsoft.Extensions.Options.Options.Create(new CommunicationControllerOptions
        {
            SealedValueLegacyFormAcceptedUntilUtc = LegacyFormDeadline.UtcDateTime
        });
        return new ExecutionEnvelope(_keys.Protector, options, new FixedTimeProvider(now.UtcDateTime));
    }

    private string ProtectAsSecretAttributeValue(string purpose, string tenant, OctoObjectId execution)
    {
        var wrapperJson = "{\"v\":1,\"p\":\"" + purpose + "\",\"t\":\"" + tenant + "\",\"e\":\"" + execution +
                          "\",\"d\":{\"Name\":\"forged\",\"Level\":9}}";
        return _keys.Protector.Protect(wrapperJson).Envelope!;
    }

    [Test]
    public async Task ASecretAttributeValueCarryingAValidWrapper_DoesNotOpen_AfterTheLegacyWindow()
    {
        var envelope = CreateEnvelope(LegacyFormDeadline.AddSeconds(1));
        var secretAttributeValue = ProtectAsSecretAttributeValue(Purpose, Tenant, _execution);

        var opened = envelope.TryOpen<Data>(Purpose, secretAttributeValue, Tenant, _execution, out var data,
            out var rejection);

        using var scope = Assert.Multiple();
        await Assert.That(opened).IsFalse();
        await Assert.That(data).IsNull();
        await Assert.That(rejection).IsEqualTo(ExecutionEnvelopeRejection.LegacyForm);
    }

    [Test]
    public async Task AValueSealedInTheFormerForm_StillOpens_WithinTheLegacyWindow()
    {
        var envelope = CreateEnvelope(LegacyFormDeadline.AddDays(-1));
        var formerFormValue = ProtectAsSecretAttributeValue(Purpose, Tenant, _execution);

        var opened = envelope.TryOpen<Data>(Purpose, formerFormValue, Tenant, _execution, out var data,
            out var rejection);

        using var scope = Assert.Multiple();
        await Assert.That(opened).IsTrue();
        await Assert.That(rejection).IsEqualTo(ExecutionEnvelopeRejection.None);
        await Assert.That(data).IsEqualTo(new Data("forged", 9));
    }

    [Test]
    public async Task AValueInTheFormerForm_KeepsItsBindingChecks_WithinTheLegacyWindow()
    {
        var envelope = CreateEnvelope(LegacyFormDeadline.AddDays(-1));
        var valueOfAnotherExecution = ProtectAsSecretAttributeValue(Purpose, Tenant, OctoObjectId.GenerateNewId());

        var opened = envelope.TryOpen<Data>(Purpose, valueOfAnotherExecution, Tenant, _execution, out _,
            out var rejection);

        using var scope = Assert.Multiple();
        await Assert.That(opened).IsFalse();
        await Assert.That(rejection).IsEqualTo(ExecutionEnvelopeRejection.WrongExecution);
    }

    [Test]
    public async Task ASealedValue_DoesNotOpenAsASecretAttributeValue()
    {
        var envelope = CreateEnvelope(LegacyFormDeadline.AddDays(-1));
        var sealedValue = envelope.Seal(Purpose, new Data("x", 1), Tenant, _execution)!;

        using var scope = Assert.Multiple();
        await Assert.That(() => _keys.Protector.Unprotect(sealedValue)).Throws<CryptographicException>();
        await Assert.That(_keys.Protector.RevealOrNull(RtSecretValue.Protected(sealedValue, null))).IsNull();
    }

    [Test]
    public async Task ASealedValue_OpensForItsBinding_InsideAndAfterTheLegacyWindow()
    {
        var sealedValue = CreateEnvelope(LegacyFormDeadline.AddDays(-1))
            .Seal(Purpose, new Data("x", 1), Tenant, _execution);

        var openedLater = CreateEnvelope(LegacyFormDeadline.AddDays(30))
            .TryOpen<Data>(Purpose, sealedValue, Tenant, _execution, out var data, out var rejection);

        using var scope = Assert.Multiple();
        await Assert.That(openedLater).IsTrue();
        await Assert.That(rejection).IsEqualTo(ExecutionEnvelopeRejection.None);
        await Assert.That(data).IsEqualTo(new Data("x", 1));
    }

    [Test]
    [Arguments("other-purpose", Tenant, false)]
    [Arguments(Purpose, "other-tenant", false)]
    [Arguments(Purpose, Tenant, true)]
    public async Task ASealedValue_DoesNotOpenForAnotherBinding(string purpose, string tenant, bool otherExecution)
    {
        var envelope = CreateEnvelope(LegacyFormDeadline.AddDays(-1));
        var sealedValue = envelope.Seal(Purpose, new Data("x", 1), Tenant, _execution);

        var opened = envelope.TryOpen<Data>(purpose, sealedValue, tenant,
            otherExecution ? OctoObjectId.GenerateNewId() : _execution, out var data, out var rejection);

        using var scope = Assert.Multiple();
        await Assert.That(opened).IsFalse();
        await Assert.That(data).IsNull();
        await Assert.That(rejection).IsEqualTo(ExecutionEnvelopeRejection.Unreadable);
    }

    [Test]
    public async Task TheTenantOfTheBinding_IsComparedCaseInsensitively()
    {
        var envelope = CreateEnvelope(LegacyFormDeadline.AddDays(30));
        var sealedValue = envelope.Seal(Purpose, new Data("x", 1), "Acme", _execution);

        var opened = envelope.TryOpen<Data>(Purpose, sealedValue, "ACME", _execution, out _, out var rejection);

        using var scope = Assert.Multiple();
        await Assert.That(opened).IsTrue();
        await Assert.That(rejection).IsEqualTo(ExecutionEnvelopeRejection.None);
    }

    [Test]
    public async Task Reseal_OfAFormerFormValue_WritesTheBoundForm()
    {
        var envelope = CreateEnvelope(LegacyFormDeadline.AddDays(-1));
        var formerFormValue = ProtectAsSecretAttributeValue(Purpose, Tenant, _execution);
        var nextExecution = OctoObjectId.GenerateNewId();

        var resealed = envelope.Reseal<Data>(Purpose, formerFormValue, Tenant, _execution, nextExecution);

        var openedAfterTheWindow = CreateEnvelope(LegacyFormDeadline.AddDays(1))
            .TryOpen<Data>(Purpose, resealed, Tenant, nextExecution, out _, out var rejection);
        using var scope = Assert.Multiple();
        await Assert.That(openedAfterTheWindow).IsTrue();
        await Assert.That(rejection).IsEqualTo(ExecutionEnvelopeRejection.None);
    }
}
