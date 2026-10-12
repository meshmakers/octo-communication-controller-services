using System.Security.Cryptography;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Options;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Runtime.Contracts.Secrets;
using Meshmakers.Octo.Sdk.Common.Encryption;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;

/// <summary>
/// AB#5537: the REAL engine key ring (<see cref="ISecretAttributeProtector" />, registered by
/// <c>AddRuntimeEngine()</c>) with freshly generated keys — no fixed key material in tests.
/// </summary>
internal sealed class TestSecretEncryption
{
    private TestSecretEncryption(string? instanceKeyBase64, ISecretAttributeProtector protector,
        IServiceProvider serviceProvider)
    {
        InstanceKeyBase64 = instanceKeyBase64;
        Protector = protector;
        ServiceProvider = serviceProvider;
        EncryptionService = new WorkloadEncryptionService(
            Microsoft.Extensions.Options.Options.Create(new CommunicationControllerOptions
                { InstanceSecretKey = instanceKeyBase64 }),
            new InstanceSecretCrypto(protector), protector);
    }

    /// <summary>The legacy instance key (base64), also the ring's <c>k1</c> / <c>LegacyV1Key</c> when shared.</summary>
    public string? InstanceKeyBase64 { get; }

    /// <summary>The engine's protector.</summary>
    public ISecretAttributeProtector Protector { get; }

    /// <summary>The provider the protector was resolved from (AddRuntimeEngine + configuration).</summary>
    public IServiceProvider ServiceProvider { get; }

    /// <summary>The controller's encryption/reveal service on top of both keys.</summary>
    public WorkloadEncryptionService EncryptionService { get; }

    /// <summary>A fresh base64 32-byte key.</summary>
    public static string NewKeyBase64() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    /// <summary>
    /// Key ring <c>k1</c> active; by default <c>k1</c> = <c>LegacyV1Key</c> = instance key, as on a
    /// real cluster (concept decision 3).
    /// </summary>
    /// <param name="withKeyRing">false = no SecretEncryption at all (protector not configured)</param>
    /// <param name="withInstanceKey">false = no CommunicationController:InstanceSecretKey</param>
    /// <param name="withLegacyKey">false = the ring carries no LegacyV1Key</param>
    /// <param name="keyId">Key id of the ring's only (active) key; another id simulates a foreign environment</param>
    public static TestSecretEncryption Create(bool withKeyRing = true, bool withInstanceKey = true,
        bool withLegacyKey = true, string keyId = "k1")
    {
        var sharedKey = NewKeyBase64();
        var settings = new Dictionary<string, string?>();
        if (withKeyRing)
        {
            settings[$"SecretEncryption:Keys:{keyId}"] = sharedKey;
            settings["SecretEncryption:ActiveKeyId"] = keyId;
            if (withLegacyKey)
            {
                settings["SecretEncryption:LegacyV1Key"] = sharedKey;
            }
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddRuntimeEngine();
        services.AddSingleton<IInstanceSecretCrypto, InstanceSecretCrypto>();
        var provider = services.BuildServiceProvider();
        return new TestSecretEncryption(withInstanceKey ? sharedKey : null,
            provider.GetRequiredService<ISecretAttributeProtector>(), provider);
    }

    /// <summary>A legacy <c>enc:v1</c> ciphertext under the instance key.</summary>
    public string EncryptV1(string plaintext)
    {
        return new InstanceSecretCrypto().Encrypt(Convert.FromBase64String(InstanceKeyBase64!), plaintext);
    }

    /// <summary>Options carrying the instance key, for services constructed by hand.</summary>
    public IOptions<CommunicationControllerOptions> ControllerOptions =>
        Microsoft.Extensions.Options.Options.Create(new CommunicationControllerOptions
            { InstanceSecretKey = InstanceKeyBase64 });
}
