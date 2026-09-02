using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Hubs;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v3;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.PoolServiceTests;

/// <summary>
/// AB#5072: the adapter can authenticate its own <c>/{tenantId}/adapterHub</c> connection, but only
/// if the credentials reach the pod. This suite pins the controller half of that route — the
/// projection of the adapter's provisioned <c>ServiceAccountConfiguration</c> (AB#5027) into the
/// <c>ValueOverride[]</c> the operator turns into Helm values.
///
/// 🔴 The two value paths are the ONLY coupling to the chart in
/// <c>octo-mesh-adapter/src/charts/octo-mesh-adapter/templates/_env.tpl</c>, and a typo in either is
/// invisible until a pod is running (the adapter simply connects anonymously). They are therefore
/// asserted as literals here, not through the constants — asserting a constant against itself would
/// pin nothing.
/// </summary>
internal class DeployWorkloadServiceAccountCredentialsTests : PoolServiceTestsBase
{
    private const string ChartClientIdPath = "serviceAccountClientId";
    private const string ChartClientSecretPath = "secrets.serviceAccountClientSecret";

    private RtPool ArrangeCloudPool()
    {
        return new RtPool
        {
            RtId = OctoObjectId.GenerateNewId(),
            CkTypeId = SystemCommunicationCkIds.RtCkPoolTypeId,
            Name = "cloud-pool",
            Environment = RtEnvironmentEnum.Cloud
        };
    }

    private void ArrangeDeployableWorkload(RtPool pool, RtDeployableWorkload workload)
    {
        workload.ChartName = "octo-mesh-adapter";
        workload.ChartVersion = "1.0.0";

        CommunicationRepository.GetWorkloadByRtIdAsync(TenantId, workload.RtId).Returns(workload);
        CommunicationRepository.GetPoolForWorkloadAsync(TenantId, workload.RtId).Returns(pool);
        CommunicationRepository.GetHelmRepositoryForWorkloadAsync(TenantId, workload.RtId)
            .Returns(new RtHelmRepositoryConfiguration
            {
                RtId = OctoObjectId.GenerateNewId(),
                CkTypeId = SystemCommunicationCkIds.RtCkHelmRepositoryConfigurationTypeId,
                RepositoryUrl = "https://charts.example.com"
            });
    }

    private RtServiceAccountConfiguration ArrangeServiceAccount(RtAdapter adapter,
        string clientId = "octo-pipeline-sa-1", string clientSecret = "the-plaintext-secret")
    {
        var configuration = RtEntityCreator.CreateServiceAccountConfiguration(
            PipelineServiceAccountProvisioningService.BuildWellKnownName(adapter.RtId));
        configuration.ClientId = clientId;
        configuration.ClientSecret = clientSecret;
        ServiceAccountResolver.GetAdapterDefaultAsync(TenantId, adapter.RtId).Returns(configuration);
        return configuration;
    }

    private async Task<WorkloadDeployedDto> DeployAndCaptureAsync(RtDeployableWorkload workload)
    {
        await PoolService.DeployWorkloadAsync(TenantId, workload.RtId);

        return (WorkloadDeployedDto)OperatorConnectionManager.ReceivedCalls()
            .Single(c => c.GetMethodInfo().Name ==
                         nameof(IOperatorConnectionManager.NotifyWorkloadDeployedAsync))
            .GetArguments()[0]!;
    }

    [Test]
    public async Task DeployWorkloadAsync_Adapter_ProjectsClientIdAndSecretOntoTheChartPaths()
    {
        var pool = ArrangeCloudPool();
        var adapter = RtEntityCreator.CreateAdapter();
        ArrangeDeployableWorkload(pool, adapter);
        ArrangeServiceAccount(adapter);

        var dto = await DeployAndCaptureAsync(adapter);

        var clientId = dto.Values.SingleOrDefault(v => v.Path == ChartClientIdPath);
        var clientSecret = dto.Values.SingleOrDefault(v => v.Path == ChartClientSecretPath);

        using var _ = Assert.Multiple();
        await Assert.That(clientId).IsNotNull();
        await Assert.That(clientId!.Value).IsEqualTo("octo-pipeline-sa-1");
        // Non-secret: a client id is public, and rendering it as a literal keeps it visible in a
        // `kubectl describe pod`, which is how an operator tells a configured adapter from one that
        // will connect anonymously.
        await Assert.That(clientId.IsSecret).IsFalse();

        await Assert.That(clientSecret).IsNotNull();
        await Assert.That(clientSecret!.Value).IsEqualTo("the-plaintext-secret");
        // 🔴 Secret-flagged is what makes the operator materialise it into {release}-octo-secrets and
        // hand the chart a valueFrom.secretKeyRef instead of an inline literal.
        await Assert.That(clientSecret.IsSecret).IsTrue();
    }

    [Test]
    public async Task DeployWorkloadAsync_Application_ProjectsNoCredentials()
    {
        var pool = ArrangeCloudPool();
        var application = new RtApplication
        {
            RtId = OctoObjectId.GenerateNewId(),
            CkTypeId = SystemCommunicationCkIds.RtCkApplicationTypeId,
            Name = "some-app"
        };
        ArrangeDeployableWorkload(pool, application);

        var dto = await DeployAndCaptureAsync(application);

        using var _ = Assert.Multiple();
        // An Application executes no pipelines, connects to no adapter hub, and its CK type does not
        // even carry the PipelineServiceAccount association.
        await Assert.That(dto.Values).IsEmpty();
        await ServiceAccountResolver.DidNotReceiveWithAnyArgs()
            .GetAdapterDefaultAsync(Arg.Any<string>(), Arg.Any<OctoObjectId>());
    }

    [Test]
    public async Task DeployWorkloadAsync_NoServiceAccountLinked_ProjectsNothingAndStillDeploys()
    {
        var pool = ArrangeCloudPool();
        var adapter = RtEntityCreator.CreateAdapter();
        ArrangeDeployableWorkload(pool, adapter);
        // Base default: the resolver returns null.

        var dto = await DeployAndCaptureAsync(adapter);

        // The pre-AB#5072 shape: the workload deploys and the adapter connects anonymously, exactly
        // as the whole fleet does today. Never a failed deploy.
        await Assert.That(dto.Values).IsEmpty();
    }

    [Test]
    public async Task DeployWorkloadAsync_HalfConfiguredServiceAccount_ProjectsNothing()
    {
        var pool = ArrangeCloudPool();
        var adapter = RtEntityCreator.CreateAdapter();
        ArrangeDeployableWorkload(pool, adapter);
        // A provisioning run that was interrupted between creating the entity and writing the
        // secret. Reading ClientSecret through the generated property would throw
        // InvalidAttributeValueException (the attribute is mandatory on the CK type); the projection
        // must degrade to "no credentials" instead of failing the deploy.
        var halfConfigured = new RtServiceAccountConfiguration
        {
            RtId = OctoObjectId.GenerateNewId(),
            CkTypeId = SystemCommunicationCkIds.RtCkServiceAccountConfigurationTypeId,
            RtWellKnownName = PipelineServiceAccountProvisioningService.BuildWellKnownName(adapter.RtId),
            ClientId = "octo-pipeline-sa-1"
        };
        ServiceAccountResolver.GetAdapterDefaultAsync(TenantId, adapter.RtId).Returns(halfConfigured);

        var dto = await DeployAndCaptureAsync(adapter);

        await Assert.That(dto.Values).IsEmpty();
    }

    [Test]
    public async Task DeployWorkloadAsync_ResolverThrows_DeploysWithoutCredentials()
    {
        var pool = ArrangeCloudPool();
        var adapter = RtEntityCreator.CreateAdapter();
        ArrangeDeployableWorkload(pool, adapter);
        ServiceAccountResolver.GetAdapterDefaultAsync(TenantId, adapter.RtId)
            .ThrowsAsync(new InvalidOperationException("CK cache is being unloaded"));

        var dto = await DeployAndCaptureAsync(adapter);

        // A CK-cache hiccup must not make a workload undeployable.
        await Assert.That(dto.Values).IsEmpty();
    }

    [Test]
    public async Task DeployWorkloadAsync_ReceivesClusterSecretsFalse_StillProjectsTheCredentials()
    {
        var pool = ArrangeCloudPool();
        var adapter = RtEntityCreator.CreateAdapter();
        // The edge case that decides the gating question: a pure edge adapter must NOT receive the
        // cluster's data-store credentials, but it needs its OWN identity more than an in-cluster
        // one — it is the only credential it presents when dialling into the controller across the
        // network. Same reasoning that makes the RabbitMQ password unconditional.
        adapter.ReceivesClusterSecrets = false;
        ArrangeDeployableWorkload(pool, adapter);
        ArrangeServiceAccount(adapter);

        var dto = await DeployAndCaptureAsync(adapter);

        using var _ = Assert.Multiple();
        await Assert.That(dto.ReceivesClusterSecrets).IsFalse();
        await Assert.That(dto.Values.Any(v => v.Path == ChartClientIdPath)).IsTrue();
        await Assert.That(dto.Values.Any(v => v.Path == ChartClientSecretPath)).IsTrue();
    }

    [Test]
    public async Task DeployWorkloadAsync_WorkloadPinsTheSamePath_KeepsTheManualOverride()
    {
        var pool = ArrangeCloudPool();
        var adapter = RtEntityCreator.CreateAdapter();
        adapter.Values = new AttributeRecordValueList<RtValueOverrideRecord>
        {
            new RtValueOverrideRecord
            {
                Path = ChartClientIdPath,
                Value = "hand-pinned-client",
                IsSecret = false
            }
        };
        ArrangeDeployableWorkload(pool, adapter);
        ArrangeServiceAccount(adapter);

        var dto = await DeployAndCaptureAsync(adapter);

        using var _ = Assert.Multiple();
        // WorkloadOverrideYamlBuilder is last-wins, so appending unconditionally would silently
        // overrule a deliberate pin an operator put on the entity.
        await Assert.That(dto.Values.Count(v => v.Path == ChartClientIdPath)).IsEqualTo(1);
        await Assert.That(dto.Values.Single(v => v.Path == ChartClientIdPath).Value)
            .IsEqualTo("hand-pinned-client");
        // The unpinned half is still supplied.
        await Assert.That(dto.Values.Any(v => v.Path == ChartClientSecretPath)).IsTrue();
    }

    [Test]
    public async Task DeployWorkloadAsync_EncryptedClientSecret_ReachesTheWireDecrypted()
    {
        var pool = ArrangeCloudPool();
        var adapter = RtEntityCreator.CreateAdapter();
        ArrangeDeployableWorkload(pool, adapter);
        ArrangeServiceAccount(adapter, clientSecret: "enc:v1:cipher");
        EncryptionService.Decrypt("enc:v1:cipher").Returns("plaintext-secret");

        var dto = await DeployAndCaptureAsync(adapter);

        // The provisioning path writes plaintext today, so Decrypt is normally a pass-through — but
        // the projection sits in the same lane as every other secret leaving this service, so an
        // encrypted value would still reach the operator usable.
        await Assert.That(dto.Values.Single(v => v.Path == ChartClientSecretPath).Value)
            .IsEqualTo("plaintext-secret");
    }

    [Test]
    [NotInParallel(nameof(DeployWorkloadServiceAccountCredentialsTests))]
    public async Task DeployWorkloadAsync_NeverWritesTheClientSecretToAnyLogTarget()
    {
        const string secret = "sJ8k2p-QmZ4x7vNb1LcT0aRwEyUiOpAsDfGhJkLzXcVbNm";

        var pool = ArrangeCloudPool();
        var adapter = RtEntityCreator.CreateAdapter();
        adapter.Name = "mesh-adapter";
        ArrangeDeployableWorkload(pool, adapter);
        ArrangeServiceAccount(adapter, clientSecret: secret);

        var memoryTarget = new NLog.Targets.MemoryTarget("deploy-credentials-secret-probe")
        {
            Layout = "${level}|${message}|${exception:format=ToString}"
        };
        var previousConfiguration = NLog.LogManager.Configuration;
        var probeConfiguration = new NLog.Config.LoggingConfiguration();
        probeConfiguration.AddRule(NLog.LogLevel.Trace, NLog.LogLevel.Fatal, memoryTarget);
        NLog.LogManager.Configuration = probeConfiguration;
        try
        {
            var dto = await DeployAndCaptureAsync(adapter);

            using var _ = Assert.Multiple();
            // The value really did travel — otherwise the probe proves nothing.
            await Assert.That(dto.Values.Single(v => v.Path == ChartClientSecretPath).Value)
                .IsEqualTo(secret);
            // Not verbatim, and not truncated either — a prefix is still secret material.
            await Assert.That(memoryTarget.Logs.Any(l => l.Contains(secret, StringComparison.Ordinal)))
                .IsFalse();
            await Assert.That(memoryTarget.Logs.Any(l => l.Contains(secret[..8], StringComparison.Ordinal)))
                .IsFalse();
            // The probe is only meaningful if the run actually logged something.
            await Assert.That(memoryTarget.Logs).IsNotEmpty();
        }
        finally
        {
            NLog.LogManager.Configuration = previousConfiguration;
        }
    }

    [Test]
    public async Task TheValuePathsMatchTheAdapterChart()
    {
        // The chart reads .Values.serviceAccountClientId into OCTO_ADAPTER__CLIENTID and
        // .Values.secrets.serviceAccountClientSecret through octo-mesh.secretEnv into
        // OCTO_ADAPTER__CLIENTSECRET. Nothing at build or deploy time notices a drift here.
        using var _ = Assert.Multiple();
        await Assert.That(PoolService.ServiceAccountClientIdValuePath).IsEqualTo(ChartClientIdPath);
        await Assert.That(PoolService.ServiceAccountClientSecretValuePath).IsEqualTo(ChartClientSecretPath);
    }
}
