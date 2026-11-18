using System.Security.Cryptography;
using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;
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
///     value never opens as the value of a Secret attribute. A value in the former form (bound only by its content)
///     is refused like any other value that does not open: it cannot be told apart from a Secret attribute value.
/// </summary>
internal class ExecutionEnvelopePurposeBindingTests
{
    private const string Tenant = "acme";
    private const string Purpose = ExecutionEnvelopePurposes.QueuedCaller;

    private readonly OctoObjectId _execution = OctoObjectId.GenerateNewId();
    private readonly TestSecretEncryption _keys = TestSecretEncryption.Create();

    private sealed record Data(string Name, int Level);

    private ExecutionEnvelope CreateEnvelope()
    {
        return new ExecutionEnvelope(_keys.Protector);
    }

    private string ProtectAsSecretAttributeValue(string purpose, string tenant, OctoObjectId execution)
    {
        var wrapperJson = "{\"v\":1,\"p\":\"" + purpose + "\",\"t\":\"" + tenant + "\",\"e\":\"" + execution +
                          "\",\"d\":{\"Name\":\"forged\",\"Level\":9}}";
        return _keys.Protector.Protect(wrapperJson).Envelope!;
    }

    [Test]
    public async Task ASecretAttributeValueCarryingAValidWrapper_DoesNotOpen()
    {
        var envelope = CreateEnvelope();
        var secretAttributeValue = ProtectAsSecretAttributeValue(Purpose, Tenant, _execution);

        var opened = envelope.TryOpen<Data>(Purpose, secretAttributeValue, Tenant, _execution, out var data,
            out var rejection);

        using var scope = Assert.Multiple();
        await Assert.That(opened).IsFalse();
        await Assert.That(data).IsNull();
        await Assert.That(rejection).IsEqualTo(ExecutionEnvelopeRejection.Unreadable);
    }

    [Test]
    public async Task ASecretAttributeValueCarryingAValidWrapper_IsNotResealed()
    {
        var envelope = CreateEnvelope();
        var secretAttributeValue = ProtectAsSecretAttributeValue(Purpose, Tenant, _execution);
        var nextExecution = OctoObjectId.GenerateNewId();

        var resealed = envelope.Reseal<Data>(Purpose, secretAttributeValue, Tenant, _execution, nextExecution);

        await Assert.That(resealed).IsNull();
    }

    [Test]
    public async Task ASealedValue_DoesNotOpenAsASecretAttributeValue()
    {
        var envelope = CreateEnvelope();
        var sealedValue = envelope.Seal(Purpose, new Data("x", 1), Tenant, _execution)!;

        using var scope = Assert.Multiple();
        await Assert.That(() => _keys.Protector.Unprotect(sealedValue)).Throws<CryptographicException>();
        await Assert.That(_keys.Protector.RevealOrNull(RtSecretValue.Protected(sealedValue, null))).IsNull();
    }

    [Test]
    public async Task ASealedValue_OpensForItsBinding()
    {
        var envelope = CreateEnvelope();
        var sealedValue = envelope.Seal(Purpose, new Data("x", 1), Tenant, _execution);

        var opened = envelope.TryOpen<Data>(Purpose, sealedValue, Tenant, _execution, out var data,
            out var rejection);

        using var scope = Assert.Multiple();
        await Assert.That(opened).IsTrue();
        await Assert.That(rejection).IsEqualTo(ExecutionEnvelopeRejection.None);
        await Assert.That(data).IsEqualTo(new Data("x", 1));
    }

    [Test]
    [Arguments("other-purpose", Tenant, false)]
    [Arguments(Purpose, "other-tenant", false)]
    [Arguments(Purpose, Tenant, true)]
    public async Task ASealedValue_DoesNotOpenForAnotherBinding(string purpose, string tenant, bool otherExecution)
    {
        var envelope = CreateEnvelope();
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
        var envelope = CreateEnvelope();
        var sealedValue = envelope.Seal(Purpose, new Data("x", 1), "Acme", _execution);

        var opened = envelope.TryOpen<Data>(Purpose, sealedValue, "ACME", _execution, out _, out var rejection);

        using var scope = Assert.Multiple();
        await Assert.That(opened).IsTrue();
        await Assert.That(rejection).IsEqualTo(ExecutionEnvelopeRejection.None);
    }

    [Test]
    public async Task Reseal_OfASealedValue_OpensForTheNextExecutionOnly()
    {
        var envelope = CreateEnvelope();
        var sealedValue = envelope.Seal(Purpose, new Data("x", 1), Tenant, _execution);
        var nextExecution = OctoObjectId.GenerateNewId();

        var resealed = envelope.Reseal<Data>(Purpose, sealedValue, Tenant, _execution, nextExecution);

        var openedForTheNextExecution = envelope.TryOpen<Data>(Purpose, resealed, Tenant, nextExecution, out _,
            out var nextRejection);
        var openedForTheFormerExecution = envelope.TryOpen<Data>(Purpose, resealed, Tenant, _execution, out _,
            out var formerRejection);
        using var scope = Assert.Multiple();
        await Assert.That(openedForTheNextExecution).IsTrue();
        await Assert.That(nextRejection).IsEqualTo(ExecutionEnvelopeRejection.None);
        await Assert.That(openedForTheFormerExecution).IsFalse();
        await Assert.That(formerRejection).IsEqualTo(ExecutionEnvelopeRejection.Unreadable);
    }
}
