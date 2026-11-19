using System.Security.Cryptography;
using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.Secrets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.ExecutionEnvelopeTests;

/// <summary>
///     AB#9228: the purpose binding survives a key rotation. A value sealed under the former active key opens after the
///     rotation while that key stays in the ring, new values are sealed under the new key, and the binding still keeps
///     a sealed value and a Secret attribute value apart under either key.
/// </summary>
internal class ExecutionEnvelopeKeyRotationTests
{
    private const string Tenant = "acme";
    private const string Purpose = ExecutionEnvelopePurposes.QueuedCaller;
    private const string FormerKeyId = "k1";
    private const string NewKeyId = "k2";

    private readonly string _formerKey = NewKeyBase64();
    private readonly string _newKey = NewKeyBase64();
    private readonly OctoObjectId _execution = OctoObjectId.GenerateNewId();

    private sealed record Data(string Name, int Level);

    private static string NewKeyBase64() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    private static ISecretAttributeProtector CreateProtector(string activeKeyId,
        params (string KeyId, string Key)[] ring)
    {
        var settings = new Dictionary<string, string?> { ["SecretEncryption:ActiveKeyId"] = activeKeyId };
        foreach (var (keyId, key) in ring)
        {
            settings[$"SecretEncryption:Keys:{keyId}"] = key;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddRuntimeEngine();
        return services.BuildServiceProvider().GetRequiredService<ISecretAttributeProtector>();
    }

    private ISecretAttributeProtector BeforeRotation() => CreateProtector(FormerKeyId, (FormerKeyId, _formerKey));

    private ISecretAttributeProtector AfterRotation() =>
        CreateProtector(NewKeyId, (FormerKeyId, _formerKey), (NewKeyId, _newKey));

    private ISecretAttributeProtector AfterTheFormerKeyLeft() => CreateProtector(NewKeyId, (NewKeyId, _newKey));

    [Test]
    public async Task AValueSealedBeforeTheRotation_OpensAfterIt_WhileTheFormerKeyStaysInTheRing()
    {
        var sealedBeforeRotation = new ExecutionEnvelope(BeforeRotation())
            .Seal(Purpose, new Data("x", 1), Tenant, _execution);
        var rotatedEnvelope = new ExecutionEnvelope(AfterRotation());

        var opened = rotatedEnvelope.TryOpen<Data>(Purpose, sealedBeforeRotation, Tenant, _execution, out var data,
            out var rejection);

        using var scope = Assert.Multiple();
        await Assert.That(sealedBeforeRotation).StartsWith($"enc:v2:{FormerKeyId}:");
        await Assert.That(opened).IsTrue();
        await Assert.That(rejection).IsEqualTo(ExecutionEnvelopeRejection.None);
        await Assert.That(data).IsEqualTo(new Data("x", 1));
    }

    [Test]
    public async Task AfterTheRotation_NewValuesAreSealedUnderTheNewKey_AndOpenForTheirBindingOnly()
    {
        var rotatedEnvelope = new ExecutionEnvelope(AfterRotation());
        var otherExecution = OctoObjectId.GenerateNewId();

        var sealedAfterRotation = rotatedEnvelope.Seal(Purpose, new Data("y", 2), Tenant, _execution);
        var openedForItsBinding = rotatedEnvelope.TryOpen<Data>(Purpose, sealedAfterRotation, Tenant, _execution,
            out var data, out var rejection);
        var openedForAnotherExecution = rotatedEnvelope.TryOpen<Data>(Purpose, sealedAfterRotation, Tenant,
            otherExecution, out _, out var otherExecutionRejection);

        using var scope = Assert.Multiple();
        await Assert.That(sealedAfterRotation).StartsWith($"enc:v2:{NewKeyId}:");
        await Assert.That(openedForItsBinding).IsTrue();
        await Assert.That(rejection).IsEqualTo(ExecutionEnvelopeRejection.None);
        await Assert.That(data).IsEqualTo(new Data("y", 2));
        await Assert.That(openedForAnotherExecution).IsFalse();
        await Assert.That(otherExecutionRejection).IsEqualTo(ExecutionEnvelopeRejection.Unreadable);
    }

    [Test]
    public async Task AValueSealedBeforeTheRotation_StillDoesNotOpenForAnotherBinding_AfterIt()
    {
        var sealedBeforeRotation = new ExecutionEnvelope(BeforeRotation())
            .Seal(Purpose, new Data("x", 1), Tenant, _execution);
        var rotatedEnvelope = new ExecutionEnvelope(AfterRotation());

        var openedForAnotherTenant = rotatedEnvelope.TryOpen<Data>(Purpose, sealedBeforeRotation, "other-tenant",
            _execution, out _, out var tenantRejection);
        var openedForAnotherExecution = rotatedEnvelope.TryOpen<Data>(Purpose, sealedBeforeRotation, Tenant,
            OctoObjectId.GenerateNewId(), out _, out var executionRejection);

        using var scope = Assert.Multiple();
        await Assert.That(openedForAnotherTenant).IsFalse();
        await Assert.That(tenantRejection).IsEqualTo(ExecutionEnvelopeRejection.Unreadable);
        await Assert.That(openedForAnotherExecution).IsFalse();
        await Assert.That(executionRejection).IsEqualTo(ExecutionEnvelopeRejection.Unreadable);
    }

    [Test]
    public async Task UnderEitherKey_ASealedValueDoesNotRevealAsASecretAttribute_AndASecretDoesNotOpenAsASealedValue()
    {
        var rotatedProtector = AfterRotation();
        var rotatedEnvelope = new ExecutionEnvelope(rotatedProtector);
        var sealedUnderFormerKey = new ExecutionEnvelope(BeforeRotation())
            .Seal(Purpose, new Data("x", 1), Tenant, _execution)!;
        var sealedUnderNewKey = rotatedEnvelope.Seal(Purpose, new Data("x", 1), Tenant, _execution)!;
        var forgedUnderFormerKey = new FormerFormExecutionEnvelope(BeforeRotation())
            .Seal(Purpose, new Data("forged", 9), Tenant, _execution);
        var forgedUnderNewKey = new FormerFormExecutionEnvelope(rotatedProtector)
            .Seal(Purpose, new Data("forged", 9), Tenant, _execution);

        var forgedFormerOpened = rotatedEnvelope.TryOpen<Data>(Purpose, forgedUnderFormerKey, Tenant, _execution,
            out _, out var forgedFormerRejection);
        var forgedNewOpened = rotatedEnvelope.TryOpen<Data>(Purpose, forgedUnderNewKey, Tenant, _execution, out _,
            out var forgedNewRejection);

        using var scope = Assert.Multiple();
        await Assert.That(rotatedProtector.RevealOrNull(RtSecretValue.Protected(sealedUnderFormerKey, null))).IsNull();
        await Assert.That(rotatedProtector.RevealOrNull(RtSecretValue.Protected(sealedUnderNewKey, null))).IsNull();
        await Assert.That(forgedFormerOpened).IsFalse();
        await Assert.That(forgedFormerRejection).IsEqualTo(ExecutionEnvelopeRejection.Unreadable);
        await Assert.That(forgedNewOpened).IsFalse();
        await Assert.That(forgedNewRejection).IsEqualTo(ExecutionEnvelopeRejection.Unreadable);
    }

    [Test]
    public async Task AValueSealedBeforeTheRotation_IsUnknownKey_OnceTheFormerKeyLeftTheRing()
    {
        var sealedBeforeRotation = new ExecutionEnvelope(BeforeRotation())
            .Seal(Purpose, new Data("x", 1), Tenant, _execution);
        var envelopeWithoutFormerKey = new ExecutionEnvelope(AfterTheFormerKeyLeft());

        var opened = envelopeWithoutFormerKey.TryOpen<Data>(Purpose, sealedBeforeRotation, Tenant, _execution, out _,
            out var rejection);

        using var scope = Assert.Multiple();
        await Assert.That(opened).IsFalse();
        await Assert.That(rejection).IsEqualTo(ExecutionEnvelopeRejection.UnknownKey);
    }
}
