using System.Text.Json.Nodes;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;
using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.DeploymentSiteServiceTests;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services;

/// <summary>
/// AB#5537 — <c>SerializeWithRevealedSecrets</c>: the JSON an adapter receives for a configuration
/// carries the plaintext of every Secret attribute (instead of the engine's <c>{"isSet":…}</c>
/// marker), is byte-identical to <c>Serialize()</c> when there is no secret, never ships a pending
/// value and degrades an undecryptable secret to "not set" instead of failing.
/// </summary>
internal class ConfigurationSecretSerializerTests
{
    private const string TenantId = "tenantId";
    private const string Password = "sftp-pass-Ω-<&>";

    private readonly TestSecretEncryption _secrets = TestSecretEncryption.Create();

    private static RtConfiguration CreateSftpConfiguration()
    {
        var configuration = new RtConfiguration
        {
            RtId = OctoObjectId.GenerateNewId(),
            CkTypeId = SystemCommunicationCkIds.RtCkSftpConfigurationTypeId,
            RtWellKnownName = "sftp"
        };
        configuration.SetAttributeValue(nameof(RtSftpConfiguration.Host), AttributeValueTypesDto.String,
            "sftp.example.com");
        configuration.SetAttributeValue(nameof(RtSftpConfiguration.Username), AttributeValueTypesDto.String,
            "importer");
        return configuration;
    }

    private static JsonObject Parse(string json) => JsonNode.Parse(json)!.AsObject();

    [Test]
    public async Task NoSecretValues_OutputIsByteIdenticalToSerialize()
    {
        var configuration = CreateSftpConfiguration();
        configuration.SetAttributeValue(nameof(RtSftpConfiguration.Password), AttributeValueTypesDto.String,
            Password);

        var json = configuration.SerializeWithRevealedSecrets(_secrets.EncryptionService, TenantId);

        await Assert.That(json).IsEqualTo(configuration.Serialize());
    }

    [Test]
    public async Task ProtectedSecret_IsShippedAsPlaintextString()
    {
        var configuration = CreateSftpConfiguration();
        configuration.SetAttributeRawValue(nameof(RtSftpConfiguration.Password), _secrets.Protector.Protect(Password));

        var json = configuration.SerializeWithRevealedSecrets(_secrets.EncryptionService, TenantId);

        var attributes = Parse(json)["attributes"]!.AsObject();
        using var _ = Assert.Multiple();
        await Assert.That(attributes[nameof(RtSftpConfiguration.Password)]!.GetValue<string>()).IsEqualTo(Password);
        await Assert.That(attributes[nameof(RtSftpConfiguration.Host)]!.GetValue<string>())
            .IsEqualTo("sftp.example.com");
        await Assert.That(json.Contains("isSet", StringComparison.Ordinal)).IsFalse();
        await Assert.That(json.Contains("enc:v2", StringComparison.Ordinal)).IsFalse();
    }

    /// <summary>
    /// AB#7506 — an <c>MqttConfiguration</c> ships like every other connection configuration: the
    /// broker password arrives revealed, the rest of the tuple is untouched.
    /// </summary>
    [Test]
    public async Task MqttConfiguration_ShipsBrokerPasswordRevealed()
    {
        var configuration = new RtConfiguration
        {
            RtId = OctoObjectId.GenerateNewId(),
            CkTypeId = SystemCommunicationCkIds.RtCkMqttConfigurationTypeId,
            RtWellKnownName = "mqtt"
        };
        configuration.SetAttributeValue(nameof(RtMqttConfiguration.Host), AttributeValueTypesDto.String,
            "broker.example.com");
        configuration.SetAttributeValue(nameof(RtMqttConfiguration.Port), AttributeValueTypesDto.Int, 8883);
        configuration.SetAttributeValue(nameof(RtMqttConfiguration.Username), AttributeValueTypesDto.String,
            "edge");
        configuration.SetAttributeValue(nameof(RtMqttConfiguration.IsSslEnabled), AttributeValueTypesDto.Boolean,
            true);
        configuration.SetAttributeRawValue(nameof(RtMqttConfiguration.Password),
            _secrets.Protector.Protect(Password));

        var json = configuration.SerializeWithRevealedSecrets(_secrets.EncryptionService, TenantId);

        var attributes = Parse(json)["attributes"]!.AsObject();
        using var _ = Assert.Multiple();
        await Assert.That(attributes[nameof(RtMqttConfiguration.Password)]!.GetValue<string>()).IsEqualTo(Password);
        await Assert.That(attributes[nameof(RtMqttConfiguration.Host)]!.GetValue<string>())
            .IsEqualTo("broker.example.com");
        await Assert.That(attributes[nameof(RtMqttConfiguration.Port)]!.GetValue<int>()).IsEqualTo(8883);
        await Assert.That(attributes[nameof(RtMqttConfiguration.IsSslEnabled)]!.GetValue<bool>()).IsTrue();
        await Assert.That(json.Contains("isSet", StringComparison.Ordinal)).IsFalse();
        await Assert.That(json.Contains("enc:v2", StringComparison.Ordinal)).IsFalse();
    }

    /// <summary>
    /// The shape an adapter node deserialises a configuration into (cf. the mesh adapter's
    /// <c>FromEmailNode.EmailServerConfiguration</c>): plain top-level properties.
    /// </summary>
    // ReSharper disable once ClassNeverInstantiated.Local
    private sealed record AdapterSftpConfiguration
    {
        // ReSharper disable UnusedAutoPropertyAccessor.Local
        public required string Host { get; init; }
        public required string Username { get; init; }
        public string? Password { get; init; }
        // ReSharper restore UnusedAutoPropertyAccessor.Local
    }

    [Test]
    public async Task ShippedJson_RoundTripsIntoTheTypedConfiguration()
    {
        // What the adapter does with it: IGlobalConfiguration.GetValue<T> runs the shipped JSON
        // through Meshmakers.Common.Shared's Deserialize<T> into its own typed configuration.
        var configuration = new RtSftpConfiguration
        {
            RtId = OctoObjectId.GenerateNewId(),
            CkTypeId = SystemCommunicationCkIds.RtCkSftpConfigurationTypeId,
            RtWellKnownName = "sftp",
            Host = "sftp.example.com",
            Username = "importer"
        };
        configuration.SetAttributeRawValue(nameof(RtSftpConfiguration.Password), _secrets.Protector.Protect(Password));

        var json = configuration.SerializeWithRevealedSecrets(_secrets.EncryptionService, TenantId);
        var typed = json.Deserialize<AdapterSftpConfiguration>();

        using var _ = Assert.Multiple();
        await Assert.That(typed.Password).IsEqualTo(Password);
        await Assert.That(typed.Host).IsEqualTo("sftp.example.com");
        await Assert.That(typed.Username).IsEqualTo("importer");
    }

    [Test]
    public async Task LegacyAndEncryptedStringForms_AreAllRevealed()
    {
        var configuration = CreateSftpConfiguration();
        configuration.SetAttributeRawValue(nameof(RtSftpConfiguration.Password),
            RtSecretValue.LegacyPlaintext(_secrets.EncryptV1(Password)));
        configuration.SetAttributeRawValue(nameof(RtSftpConfiguration.PrivateKey),
            RtSecretValue.LegacyPlaintext("-----BEGIN KEY-----"));

        var attributes = Parse(configuration.SerializeWithRevealedSecrets(_secrets.EncryptionService, TenantId))
            ["attributes"]!.AsObject();

        using var _ = Assert.Multiple();
        await Assert.That(attributes[nameof(RtSftpConfiguration.Password)]!.GetValue<string>()).IsEqualTo(Password);
        await Assert.That(attributes[nameof(RtSftpConfiguration.PrivateKey)]!.GetValue<string>())
            .IsEqualTo("-----BEGIN KEY-----");
    }

    [Test]
    public async Task PendingSecret_NeverLeaves()
    {
        var configuration = CreateSftpConfiguration();
        configuration.SetAttributeRawValue(nameof(RtSftpConfiguration.Password), RtSecretValue.Pending(Password));

        var json = configuration.SerializeWithRevealedSecrets(_secrets.EncryptionService, TenantId);

        using var _ = Assert.Multiple();
        await Assert.That(Parse(json)["attributes"]![nameof(RtSftpConfiguration.Password)]).IsNull();
        await Assert.That(json.Contains(Password, StringComparison.Ordinal)).IsFalse();
    }

    [Test]
    [NotInParallel(nameof(DeployWorkloadServiceAccountCredentialsTests))]
    public async Task UndecryptableSecret_ShipsAsNotSet_KeepsTheRest_AndLogsNoValue()
    {
        // Encrypted under another installation's key (decision 5: cross-environment restore).
        var foreign = TestSecretEncryption.Create();
        var configuration = CreateSftpConfiguration();
        configuration.SetAttributeRawValue(nameof(RtSftpConfiguration.Password), foreign.Protector.Protect(Password));
        configuration.SetAttributeRawValue(nameof(RtSftpConfiguration.PrivateKeyPassphrase),
            _secrets.Protector.Protect("passphrase-ok"));

        var memoryTarget = new NLog.Targets.MemoryTarget("configuration-secret-probe")
        {
            Layout = "${level}|${message}|${exception:format=ToString}"
        };
        var previousConfiguration = NLog.LogManager.Configuration;
        var probeConfiguration = new NLog.Config.LoggingConfiguration();
        probeConfiguration.AddRule(NLog.LogLevel.Trace, NLog.LogLevel.Fatal, memoryTarget);
        NLog.LogManager.Configuration = probeConfiguration;
        try
        {
            var attributes = Parse(configuration.SerializeWithRevealedSecrets(_secrets.EncryptionService, TenantId))
                ["attributes"]!.AsObject();

            using var _ = Assert.Multiple();
            await Assert.That(attributes[nameof(RtSftpConfiguration.Password)]).IsNull();
            await Assert.That(attributes[nameof(RtSftpConfiguration.PrivateKeyPassphrase)]!.GetValue<string>())
                .IsEqualTo("passphrase-ok");
            await Assert.That(memoryTarget.Logs.Any(l =>
                l.StartsWith("Error|", StringComparison.Ordinal) &&
                l.Contains(nameof(RtSftpConfiguration.Password), StringComparison.Ordinal))).IsTrue();
            await Assert.That(memoryTarget.Logs.Any(l => l.Contains("passphrase-ok", StringComparison.Ordinal)))
                .IsFalse();
            await Assert.That(memoryTarget.Logs.Any(l => l.Contains(Password, StringComparison.Ordinal))).IsFalse();
        }
        finally
        {
            NLog.LogManager.Configuration = previousConfiguration;
        }
    }

    [Test]
    [NotInParallel(nameof(DeployWorkloadServiceAccountCredentialsTests))]
    public async Task SecretWithUnknownKeyId_ShipsAsNotSet_AndLogsTheKeyIdButNoValue()
    {
        // Decisions 2026-10-06, item 2: restored from another environment - the key id is not in the ring.
        var configuration = CreateSftpConfiguration();
        configuration.SetAttributeRawValue(nameof(RtSftpConfiguration.Password),
            TestSecretEncryption.Create(keyId: "k9").Protector.Protect(Password));

        var memoryTarget = new NLog.Targets.MemoryTarget("configuration-secret-kid-probe")
        {
            Layout = "${level}|${message}|${exception:format=ToString}"
        };
        var previousConfiguration = NLog.LogManager.Configuration;
        var probeConfiguration = new NLog.Config.LoggingConfiguration();
        probeConfiguration.AddRule(NLog.LogLevel.Trace, NLog.LogLevel.Fatal, memoryTarget);
        NLog.LogManager.Configuration = probeConfiguration;
        try
        {
            var attributes = Parse(configuration.SerializeWithRevealedSecrets(_secrets.EncryptionService, TenantId))
                ["attributes"]!.AsObject();

            using var _ = Assert.Multiple();
            await Assert.That(attributes[nameof(RtSftpConfiguration.Password)]).IsNull();
            await Assert.That(memoryTarget.Logs.Any(l =>
                l.StartsWith("Error|", StringComparison.Ordinal) && l.Contains("'k9'", StringComparison.Ordinal) &&
                l.Contains(nameof(RtSftpConfiguration.Password), StringComparison.Ordinal))).IsTrue();
            await Assert.That(memoryTarget.Logs.Any(l => l.Contains(Password, StringComparison.Ordinal))).IsFalse();
        }
        finally
        {
            NLog.LogManager.Configuration = previousConfiguration;
        }
    }

    [Test]
    public async Task SecretInsideRecordArray_IsRevealedPerElement()
    {
        var configuration = CreateSftpConfiguration();
        var records = new List<RtRecord>
        {
            new(SystemCommunicationCkIds.RtCkValueOverrideRecordId, new Dictionary<string, object?>
            {
                ["Path"] = "a",
                ["Value"] = _secrets.Protector.Protect("first")
            }),
            new(SystemCommunicationCkIds.RtCkValueOverrideRecordId, new Dictionary<string, object?>
            {
                ["Path"] = "b",
                ["Value"] = "plain"
            })
        };
        configuration.SetAttributeRawValue("Values", records);

        var values = Parse(configuration.SerializeWithRevealedSecrets(_secrets.EncryptionService, TenantId))
            ["attributes"]!["Values"]!.AsArray();

        using var _ = Assert.Multiple();
        await Assert.That(values[0]!["attributes"]!["Value"]!.GetValue<string>()).IsEqualTo("first");
        await Assert.That(values[1]!["attributes"]!["Value"]!.GetValue<string>()).IsEqualTo("plain");
        await Assert.That(values[0]!["attributes"]!["Path"]!.GetValue<string>()).IsEqualTo("a");
    }
}
