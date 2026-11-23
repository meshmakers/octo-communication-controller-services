using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.ConstructionKit.Contracts;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.ExecutionEnvelopeTests;

/// <summary>
///     The binding inside the content stays checked after the binding moved into the associated data. Only a
///     holder of the key ring that passes a binding of its own choice can produce a value whose associated data
///     verifies while its content names something else, so these values are built with the protector directly. The
///     second layer is defence in depth for exactly that case, and for two bindings that read alike: the binding joins
///     purpose, tenant and execution with '/', so a purpose that contains '/' can produce the binding of another
///     purpose and tenant.
/// </summary>
internal class ExecutionEnvelopeContentBindingTests
{
    private const string Tenant = "acme";
    private const string Purpose = ExecutionEnvelopePurposes.QueuedCaller;

    private readonly OctoObjectId _execution = OctoObjectId.GenerateNewId();
    private readonly TestSecretEncryption _keys = TestSecretEncryption.Create();

    private sealed record Data(string Name, int Level);

    private string ProtectWithTheBindingOf(string contentPurpose, string contentTenant, string contentExecution)
    {
        var wrapperJson = "{\"v\":1,\"p\":\"" + contentPurpose + "\",\"t\":\"" + contentTenant + "\",\"e\":\"" +
                          contentExecution + "\",\"d\":{\"Name\":\"x\",\"Level\":1}}";
        var binding = ExecutionEnvelope.BuildBinding(Purpose, Tenant, _execution);
        return _keys.Protector.Protect(wrapperJson, binding).Envelope!;
    }

    [Test]
    [Arguments("other-purpose", Tenant, false, ExecutionEnvelopeRejection.WrongPurpose)]
    [Arguments(Purpose, "other-tenant", false, ExecutionEnvelopeRejection.WrongTenant)]
    [Arguments(Purpose, Tenant, true, ExecutionEnvelopeRejection.WrongExecution)]
    public async Task AVerifiedBinding_WhoseContentNamesSomethingElse_IsRefusedByTheContentCheck(
        string contentPurpose, string contentTenant, bool otherExecution, ExecutionEnvelopeRejection expected)
    {
        var envelope = new ExecutionEnvelope(_keys.Protector);
        var contentExecution = otherExecution ? OctoObjectId.GenerateNewId().ToString() : _execution.ToString();
        var stored = ProtectWithTheBindingOf(contentPurpose, contentTenant, contentExecution);

        var opened = envelope.TryOpen<Data>(Purpose, stored, Tenant, _execution, out var data, out var rejection);

        using var scope = Assert.Multiple();
        await Assert.That(opened).IsFalse();
        await Assert.That(data).IsNull();
        await Assert.That(rejection).IsEqualTo(expected);
    }

    [Test]
    public async Task TwoBindingsThatReadAlike_AreToldApartByTheContent()
    {
        var envelope = new ExecutionEnvelope(_keys.Protector);
        var sealedForASlashedPurpose = envelope.Seal(Purpose + "/x", new Data("x", 1), "y", _execution);

        var opened = envelope.TryOpen<Data>(Purpose, sealedForASlashedPurpose, "x/y", _execution, out var data,
            out var rejection);

        using var scope = Assert.Multiple();
        await Assert.That(ExecutionEnvelope.BuildBinding(Purpose + "/x", "y", _execution).Value)
            .IsEqualTo(ExecutionEnvelope.BuildBinding(Purpose, "x/y", _execution).Value);
        await Assert.That(opened).IsFalse();
        await Assert.That(data).IsNull();
        await Assert.That(rejection).IsEqualTo(ExecutionEnvelopeRejection.WrongPurpose);
    }
}
