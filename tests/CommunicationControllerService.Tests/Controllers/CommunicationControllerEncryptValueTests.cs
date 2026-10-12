using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Backend.CommunicationControllerServices.TenantApi.v1.Controllers;
using Meshmakers.Octo.Services.Infrastructure.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Controllers;

/// <summary>
/// AB#5537 — the deprecated <c>encrypt-value</c> endpoint keeps answering legacy <c>enc:v1</c> (its
/// output goes into String slots) and never accepts a key-ring <c>enc:v2</c> envelope, so no enc:v2
/// value can be routed into a String attribute through it.
/// </summary>
internal class CommunicationControllerEncryptValueTests
{
    private readonly TestSecretEncryption _secrets = TestSecretEncryption.Create();

    private CommunicationController CreateSut()
    {
        return new CommunicationController(NullLogger<CommunicationController>.Instance,
            Substitute.For<IConfigurationService>(), Substitute.For<IExpressionValidationService>(),
            _secrets.EncryptionService, Substitute.For<IWorkloadTemplateResolver>());
    }

    [Test]
    public async Task Plaintext_IsAnsweredAsEncV1_EvenWithTheKeyRingConfigured()
    {
        var result = CreateSut().EncryptValue(new EncryptValueRequest("value-to-protect"));

        var response = (EncryptValueResponse)((OkObjectResult)result).Value!;
        using var _ = Assert.Multiple();
        await Assert.That(response.Ciphertext).StartsWith("enc:v1:");
        await Assert.That(_secrets.EncryptionService.Decrypt(response.Ciphertext)).IsEqualTo("value-to-protect");
    }

    [Test]
    public async Task EncV1Input_PassesThroughUnchanged()
    {
        var cipher = _secrets.EncryptV1("value-to-protect");

        var result = CreateSut().EncryptValue(new EncryptValueRequest(cipher));

        await Assert.That(((EncryptValueResponse)((OkObjectResult)result).Value!).Ciphertext).IsEqualTo(cipher);
    }

    [Test]
    public async Task EncV2Input_IsRejected()
    {
        var envelope = _secrets.Protector.Protect("value-to-protect").Envelope!;

        var result = CreateSut().EncryptValue(new EncryptValueRequest(envelope));

        await Assert.That(result).IsTypeOf<BadRequestObjectResult>();
    }
}
