using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Hubs;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.DeploymentSiteServiceTests;

/// <summary>
/// AB#5537 — every secret the workload deploy puts on the operator wire (secret-flagged Helm
/// value overrides, the Helm repository password, the adapter's own client secret) is revealed
/// through the controller's reveal point, so it works with the String attribute (plaintext,
/// legacy enc:v1) AND with the Secret attribute of System.Communication 4.8 (RtSecretValue).
/// </summary>
internal class DeployWorkloadSecretRevealTests : PoolServiceTestsBase
{
    private const string ChartClientSecretPath = "secrets.serviceAccountClientSecret";

    private RtHelmRepositoryConfiguration _repository = null!;

    private RtAdapter ArrangeAdapterOnCloudPool()
    {
        var deploymentSite = new RtDeploymentSite
        {
            RtId = OctoObjectId.GenerateNewId(),
            CkTypeId = SystemCommunicationCkIds.RtCkDeploymentSiteTypeId,
            Name = "cloud-deploymentSite",
            Environment = RtEnvironmentEnum.Cloud
        };
        var adapter = RtEntityCreator.CreateAdapter();
        adapter.ChartName = "octo-mesh-adapter";
        adapter.ChartVersion = "1.0.0";
        _repository = new RtHelmRepositoryConfiguration
        {
            RtId = OctoObjectId.GenerateNewId(),
            CkTypeId = SystemCommunicationCkIds.RtCkHelmRepositoryConfigurationTypeId,
            RepositoryUrl = "https://charts.example.com",
            Username = "robot"
        };

        CommunicationRepository.GetWorkloadByRtIdAsync(TenantId, adapter.RtId).Returns(adapter);
        CommunicationRepository.GetDeploymentSiteForWorkloadAsync(TenantId, adapter.RtId).Returns(deploymentSite);
        CommunicationRepository.GetHelmRepositoryForWorkloadAsync(TenantId, adapter.RtId).Returns(_ => _repository);
        return adapter;
    }

    private static RtValueOverrideRecord Override(string path, object? storedValue, bool isSecret)
    {
        var record = new RtValueOverrideRecord { Path = path, IsSecret = isSecret };
        record.SetAttributeRawValue(nameof(RtValueOverrideRecord.Value), storedValue);
        return record;
    }

    private RtServiceAccountConfiguration ArrangeServiceAccount(RtAdapter adapter, object? storedSecret)
    {
        var configuration = RtEntityCreator.CreateServiceAccountConfiguration(
            PipelineServiceAccountProvisioningService.BuildWellKnownName(adapter.RtId));
        configuration.ClientId = "octo-pipeline-sa-1";
        configuration.SetAttributeRawValue(nameof(RtServiceAccountConfiguration.ClientSecret), storedSecret);
        ServiceAccountResolver.GetAdapterDefaultAsync(TenantId, adapter.RtId).Returns(configuration);
        return configuration;
    }

    private async Task<WorkloadDeployedDto> DeployAndCaptureAsync(RtDeployableWorkload workload)
    {
        await DeploymentSiteService.DeployWorkloadAsync(TenantId, workload.RtId);

        return (WorkloadDeployedDto)OperatorConnectionManager.ReceivedCalls()
            .Single(c => c.GetMethodInfo().Name == nameof(IOperatorConnectionManager.NotifyWorkloadDeployedAsync))
            .GetArguments()[0]!;
    }

    private static RtValueOverrideRecord SecretOverride(string path, RtSecretValue? secretValue, object? legacyValue)
    {
        var record = Override(path, legacyValue, isSecret: true);
        record.SecretValue = secretValue;
        return record;
    }

    [Test]
    public async Task SecretValueOverride_SecretValue_ReachesTheWireAsPlaintext()
    {
        // 4.8: a secret entry keeps its value in the Secret attribute SecretValue.
        var adapter = ArrangeAdapterOnCloudPool();
        adapter.Values = new AttributeRecordValueList<RtValueOverrideRecord>
        {
            SecretOverride("oauth.clientSecret", SecretEncryption.Protector.Protect("override-secret"), null)
        };

        var dto = await DeployAndCaptureAsync(adapter);

        var value = dto.Values.Single(v => v.Path == "oauth.clientSecret");
        using var _ = Assert.Multiple();
        await Assert.That(value.Value).IsEqualTo("override-secret");
        await Assert.That(value.IsSecret).IsTrue();
    }

    [Test]
    public async Task SecretValueOverride_SecretValueWithoutIsSecretFlag_IsStillASecret()
    {
        // Handover section 5: Studio writes SecretValue; the legacy IsSecret flag is not required for it.
        var adapter = ArrangeAdapterOnCloudPool();
        var entry = Override("oauth.clientSecret", null, isSecret: false);
        entry.SecretValue = SecretEncryption.Protector.Protect("override-secret");
        adapter.Values = new AttributeRecordValueList<RtValueOverrideRecord> { entry };

        var dto = await DeployAndCaptureAsync(adapter);

        var value = dto.Values.Single(v => v.Path == "oauth.clientSecret");
        using var _ = Assert.Multiple();
        await Assert.That(value.Value).IsEqualTo("override-secret");
        await Assert.That(value.IsSecret).IsTrue();
    }

    [Test]
    public async Task SecretValueOverride_SecretValueWinsOverALegacyValue()
    {
        var adapter = ArrangeAdapterOnCloudPool();
        adapter.Values = new AttributeRecordValueList<RtValueOverrideRecord>
        {
            SecretOverride("oauth.clientSecret", SecretEncryption.Protector.Protect("new-secret"),
                SecretEncryption.EncryptV1("old-secret"))
        };

        var dto = await DeployAndCaptureAsync(adapter);

        await Assert.That(dto.Values.Single(v => v.Path == "oauth.clientSecret").Value).IsEqualTo("new-secret");
    }

    [Test]
    public async Task SecretValueOverride_LegacyEncV1Value_StillDeploys()
    {
        // Entries written before 4.8 (Studio + encrypt-value) keep their enc:v1 Value.
        var adapter = ArrangeAdapterOnCloudPool();
        adapter.Values = new AttributeRecordValueList<RtValueOverrideRecord>
        {
            SecretOverride("oauth.clientSecret", null, SecretEncryption.EncryptV1("legacy-secret"))
        };

        var dto = await DeployAndCaptureAsync(adapter);

        await Assert.That(dto.Values.Single(v => v.Path == "oauth.clientSecret").Value).IsEqualTo("legacy-secret");
    }

    [Test]
    public async Task SecretValueOverride_EncV2EnvelopeInTheStringValue_IsRefused()
    {
        // Nothing legitimate writes enc:v2 into the String Value (encrypt-value answers enc:v1);
        // decrypting one would make Value a decryption oracle for envelopes copied from elsewhere.
        var adapter = ArrangeAdapterOnCloudPool();
        adapter.Values = new AttributeRecordValueList<RtValueOverrideRecord>
        {
            Override("oauth.clientSecret", SecretEncryption.Protector.Protect("v2-secret").Envelope, isSecret: true)
        };

        await Assert.That(async () => { await DeploymentSiteService.DeployWorkloadAsync(TenantId, adapter.RtId); })
            .Throws<Exception>();
        await OperatorConnectionManager.DidNotReceiveWithAnyArgs().NotifyWorkloadDeployedAsync(default!);
    }

    [Test]
    public async Task SecretValueOverride_CiphertextTypedIntoASecret_IsDeployedVerbatim_NeverDecryptedAgain()
    {
        // A Studio still calling encrypt-value after 4.8 and writing the result into SecretValue:
        // the engine encrypts the enc:v1 string; the reveal decrypts exactly once (no oracle).
        var adapter = ArrangeAdapterOnCloudPool();
        var ciphertext = SecretEncryption.EncryptV1("wrapped-secret");
        adapter.Values = new AttributeRecordValueList<RtValueOverrideRecord>
        {
            SecretOverride("oauth.clientSecret", SecretEncryption.Protector.Protect(ciphertext), null)
        };

        var dto = await DeployAndCaptureAsync(adapter);

        await Assert.That(dto.Values.Single(v => v.Path == "oauth.clientSecret").Value).IsEqualTo(ciphertext);
    }

    [Test]
    public async Task SecretValueOverride_DuplicateSecretPaths_FailTheDeploy()
    {
        // Path is the record key: two SecretValues under one Path make the carry-over ambiguous.
        var adapter = ArrangeAdapterOnCloudPool();
        adapter.Values = new AttributeRecordValueList<RtValueOverrideRecord>
        {
            SecretOverride("oauth.clientSecret", SecretEncryption.Protector.Protect("first"), null),
            SecretOverride("oauth.clientSecret", SecretEncryption.Protector.Protect("second"), null),
            SecretOverride("other.secret", SecretEncryption.Protector.Protect("third"), null)
        };

        var exception = await Assert.That(async () => { await DeploymentSiteService.DeployWorkloadAsync(TenantId, adapter.RtId); })
            .Throws<Exception>();

        using var _ = Assert.Multiple();
        await Assert.That(exception!.Message).Contains("'oauth.clientSecret'");
        await Assert.That(exception.Message).DoesNotContain("other.secret");
        await Assert.That(exception.Message).DoesNotContain("first");
        await OperatorConnectionManager.DidNotReceiveWithAnyArgs().NotifyWorkloadDeployedAsync(default!);
    }

    [Test]
    public async Task ValueOverride_SamePathWithoutSecretValues_StillDeploys()
    {
        // Only entries carrying a SecretValue are keyed; plain overrides keep the last-wins behaviour.
        var adapter = ArrangeAdapterOnCloudPool();
        adapter.Values = new AttributeRecordValueList<RtValueOverrideRecord>
        {
            Override("image.tag", "1.0.0", isSecret: false),
            Override("image.tag", "1.0.1", isSecret: false),
            SecretOverride("oauth.clientSecret", null, SecretEncryption.EncryptV1("legacy-a")),
            SecretOverride("oauth.clientSecret", null, SecretEncryption.EncryptV1("legacy-b"))
        };

        var dto = await DeployAndCaptureAsync(adapter);

        await Assert.That(dto.Values.Count(v => v.Path == "image.tag")).IsEqualTo(2);
    }

    [Test]
    public async Task NonSecretValueOverride_StoredAsSecret_IsRevealedAndTemplated()
    {
        var adapter = ArrangeAdapterOnCloudPool();
        adapter.Values = new AttributeRecordValueList<RtValueOverrideRecord>
        {
            Override("image.tag", SecretEncryption.Protector.Protect("1.2.3"), isSecret: false)
        };

        var dto = await DeployAndCaptureAsync(adapter);

        var value = dto.Values.Single(v => v.Path == "image.tag");
        using var _ = Assert.Multiple();
        await Assert.That(value.Value).IsEqualTo("1.2.3");
        await Assert.That(value.IsSecret).IsFalse();
    }

    [Test]
    public async Task NonSecretValueOverride_LooksEncrypted_IsTakenVerbatim()
    {
        // Unchanged contract: only IsSecret entries ever went through decryption.
        var adapter = ArrangeAdapterOnCloudPool();
        adapter.Values = new AttributeRecordValueList<RtValueOverrideRecord>
        {
            Override("note", "enc:not-really", isSecret: false)
        };

        var dto = await DeployAndCaptureAsync(adapter);

        await Assert.That(dto.Values.Single(v => v.Path == "note").Value).IsEqualTo("enc:not-really");
    }

    [Test]
    public async Task HelmRepositoryPassword_InEveryStoredForm_ReachesTheWireAsPlaintext()
    {
        foreach (var stored in new object[]
                 {
                     "plain-repo-pw",
                     SecretEncryption.EncryptV1("plain-repo-pw"),
                     SecretEncryption.EncryptionService.Encrypt("plain-repo-pw"), // enc:v1 (encrypt-value)
                     SecretEncryption.Protector.Protect("plain-repo-pw"),
                     RtSecretValue.LegacyPlaintext("plain-repo-pw")
                 })
        {
            OperatorConnectionManager.ClearReceivedCalls();
            var adapter = ArrangeAdapterOnCloudPool();
            _repository.SetAttributeRawValue(nameof(RtHelmRepositoryConfiguration.Password), stored);

            var dto = await DeployAndCaptureAsync(adapter);

            await Assert.That(dto.RepositoryPassword).IsEqualTo("plain-repo-pw");
        }
    }

    [Test]
    public async Task HelmRepositoryPassword_NotSet_IsNull()
    {
        var adapter = ArrangeAdapterOnCloudPool();

        var dto = await DeployAndCaptureAsync(adapter);

        await Assert.That(dto.RepositoryPassword).IsNull();
    }

    [Test]
    public async Task AdapterClientSecret_StoredAsSecret_ReachesTheWireAsPlaintext()
    {
        var adapter = ArrangeAdapterOnCloudPool();
        ArrangeServiceAccount(adapter, SecretEncryption.Protector.Protect("adapter-own-secret"));

        var dto = await DeployAndCaptureAsync(adapter);

        var secret = dto.Values.Single(v => v.Path == ChartClientSecretPath);
        using var _ = Assert.Multiple();
        await Assert.That(secret.Value).IsEqualTo("adapter-own-secret");
        await Assert.That(secret.IsSecret).IsTrue();
    }

    [Test]
    public async Task AdapterClientSecret_Undecryptable_DeploysWithoutCredentials()
    {
        var adapter = ArrangeAdapterOnCloudPool();
        ArrangeServiceAccount(adapter, TestSecretEncryption.Create().Protector.Protect("foreign"));

        var dto = await DeployAndCaptureAsync(adapter);

        await Assert.That(dto.Values.Any(v => v.Path == ChartClientSecretPath)).IsFalse();
    }

    // ---------------------------------------------------------------- unknown key id (decisions 2026-10-06, item 2)

    private static RtSecretValue ForeignKeyId(string plaintext) =>
        TestSecretEncryption.Create(keyId: "k9").Protector.Protect(plaintext);

    [Test]
    public async Task HelmRepositoryPassword_UnknownKeyId_IsNull()
    {
        var adapter = ArrangeAdapterOnCloudPool();
        _repository.SetAttributeRawValue(nameof(RtHelmRepositoryConfiguration.Password), ForeignKeyId("repo-pw"));

        var dto = await DeployAndCaptureAsync(adapter);

        await Assert.That(dto.RepositoryPassword).IsNull();
    }

    [Test]
    public async Task SecretValueOverride_UnknownKeyId_DeploysAsNotSet_WithoutFallingBackToTheLegacyValue()
    {
        var adapter = ArrangeAdapterOnCloudPool();
        adapter.Values = new AttributeRecordValueList<RtValueOverrideRecord>
        {
            SecretOverride("oauth.clientSecret", ForeignKeyId("override-secret"), SecretEncryption.EncryptV1("old-secret"))
        };

        var dto = await DeployAndCaptureAsync(adapter);

        var value = dto.Values.Single(v => v.Path == "oauth.clientSecret");
        using var _ = Assert.Multiple();
        await Assert.That(value.Value).IsEqualTo(string.Empty);
        await Assert.That(value.IsSecret).IsTrue();
    }

    [Test]
    public async Task AdapterClientSecret_UnknownKeyId_DeploysWithoutCredentials()
    {
        var adapter = ArrangeAdapterOnCloudPool();
        ArrangeServiceAccount(adapter, ForeignKeyId("adapter-own-secret"));

        var dto = await DeployAndCaptureAsync(adapter);

        await Assert.That(dto.Values.Any(v => v.Path == ChartClientSecretPath)).IsFalse();
    }
}
