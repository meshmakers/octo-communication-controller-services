using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Models;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Options;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Communication.Contracts;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v3;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Microsoft.Extensions.Options;
using NSubstitute;
using LocalConstants = Meshmakers.Octo.Backend.CommunicationControllerServices.Constants;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services;

/// <summary>
/// AB#5112 — the identity-health aggregate (<see cref="ServiceAccountHealthService" />): every
/// check green, each violation with its machine-readable code, the legacy (no declaration) roles
/// opt-out, and the degrade-instead-of-fail contract when the identity service cannot be asked.
/// </summary>
internal class ServiceAccountHealthTests
{
    private const string TenantId = "tenantId";

    private readonly ICommunicationRepository _repo = Substitute.For<ICommunicationRepository>();

    private readonly IPipelineServiceAccountResolver _resolver =
        Substitute.For<IPipelineServiceAccountResolver>();

    private readonly IIdentityClientReader _identityReader = Substitute.For<IIdentityClientReader>();

    private readonly CommunicationControllerOptions _options = new();

    private ServiceAccountHealthService CreateSut()
    {
        var options = Substitute.For<IOptions<CommunicationControllerOptions>>();
        options.Value.Returns(_options);
        return new ServiceAccountHealthService(_repo, _resolver, _identityReader, options);
    }

    /// <summary>
    /// A fully healthy declarative account: complete entity, portable issuer token, current
    /// tenant, declaration = [CommunicationManagement] + delegation allowed.
    /// </summary>
    private static RtServiceAccountConfiguration CreateHealthyConfiguration()
    {
        var configuration = RtEntityCreator.CreateServiceAccountConfiguration();
        configuration.IssuerUri = "{{service.authority}}";
        configuration.AssignedRoleNames =
            new AttributeStringValueList([CommonConstants.CommunicationManagementRole]);
        configuration.AllowDelegation = true;
        return configuration;
    }

    /// <summary>
    /// The AB#5115 steady-state entity: declarative, IssuerUri/TenantId ABSENT (the installation
    /// default), and optionally without a ClientSecret (the AB#5114 impersonation shape).
    /// </summary>
    private static RtServiceAccountConfiguration CreateInstallationDefaultConfiguration(bool withSecret = true)
    {
        var configuration = new RtServiceAccountConfiguration
        {
            RtId = OctoObjectId.GenerateNewId(),
            CkTypeId = SystemCommunicationCkIds.RtCkServiceAccountConfigurationTypeId,
            RtWellKnownName = "adapter-service-account",
            ClientId = "client-id",
            AssignedRoleNames = new AttributeStringValueList([CommonConstants.CommunicationManagementRole]),
            AllowDelegation = true
        };
        if (withSecret)
        {
            configuration.ClientSecret = "client-secret";
        }

        return configuration;
    }

    /// <summary>The identity answer that matches <see cref="CreateHealthyConfiguration" />.</summary>
    private void ArrangeMatchingIdentityClient()
    {
        _identityReader
            .GetClientAsync(TenantId, Arg.Any<string>(), Arg.Any<bool>())
            .Returns(callInfo => new IdentityClientLookup(IdentityClientLookupStatus.Found,
                new ClientDto
                {
                    ClientId = callInfo.ArgAt<string>(1),
                    AllowedGrantTypes = ["client_credentials", LocalConstants.OnBehalfOfGrantType]
                },
                [CommonConstants.CommunicationManagementRole], null));
    }

    private static ServiceAccountHealthCheckDto Check(ServiceAccountHealthDto dto, string name)
    {
        return dto.Checks.Single(c => c.Check == name);
    }

    // ---------------------------------------------------------------- all green

    [Test]
    public async Task GetConfigurationHealth_EverythingMatches_AllChecksHealthy()
    {
        var sut = CreateSut();
        var configuration = CreateHealthyConfiguration();
        ArrangeMatchingIdentityClient();

        var dto = await sut.GetConfigurationHealthAsync(TenantId, configuration);

        using var _ = Assert.Multiple();
        await Assert.That(dto.OverallStatus).IsEqualTo("Healthy");
        await Assert.That(dto.ConfigurationRtId).IsEqualTo(configuration.RtId.ToString());
        await Assert.That(dto.ConfigurationWellKnownName).IsEqualTo(configuration.RtWellKnownName);
        await Assert.That(dto.ClientId).IsEqualTo("client-id");
        // Config variant: no association check; everything else healthy — except the AB#5114
        // impersonation view, which is NotApplicable for an account with its own secret.
        await Assert.That(dto.Checks.Any(c => c.Check == "association")).IsFalse();
        await Assert.That(dto.Checks.Where(c => c.Check != "impersonation")
            .All(c => c.Status == "Healthy")).IsTrue();
        await Assert.That(Check(dto, "impersonation").Status).IsEqualTo("NotApplicable");
        // Declarative account → the reader is asked for the role detail.
        await _identityReader.Received(1).GetClientAsync(TenantId, "client-id", true);
    }

    [Test]
    public async Task GetAdapterHealth_EverythingMatches_IncludesHealthyAssociationCheck()
    {
        var sut = CreateSut();
        var adapter = RtEntityCreator.CreateAdapter();
        var configuration = CreateHealthyConfiguration();
        _resolver.GetAdapterDefaultAsync(TenantId, adapter.RtId).Returns(configuration);
        ArrangeMatchingIdentityClient();

        var dto = await sut.GetAdapterHealthAsync(TenantId, adapter);

        using var _ = Assert.Multiple();
        await Assert.That(dto.OverallStatus).IsEqualTo("Healthy");
        await Assert.That(Check(dto, "association").Status).IsEqualTo("Healthy");
        await Assert.That(dto.Checks.Where(c => c.Check != "impersonation")
            .All(c => c.Status == "Healthy")).IsTrue();
        await Assert.That(Check(dto, "impersonation").Status).IsEqualTo("NotApplicable");
    }

    // ---------------------------------------------------------------- association / configuration

    [Test]
    public async Task GetAdapterHealth_UnlinkedButExistingConfiguration_ReportsAssociationViolationOnly()
    {
        var sut = CreateSut();
        var adapter = RtEntityCreator.CreateAdapter();
        adapter.Name = "mesh-adapter";
        var configuration = CreateHealthyConfiguration();
        _resolver.GetAdapterDefaultAsync(TenantId, adapter.RtId)
            .Returns((RtServiceAccountConfiguration?)null);
        // The reconcile's deterministic-name adoption: the entity exists, only the edge is gone.
        _repo.GetServiceAccountByWellKnownNameAsync(TenantId,
                PipelineServiceAccountProvisioningService.BuildWellKnownName(adapter.RtId))
            .Returns(configuration);
        ArrangeMatchingIdentityClient();

        var dto = await sut.GetAdapterHealthAsync(TenantId, adapter);

        using var _ = Assert.Multiple();
        await Assert.That(dto.OverallStatus).IsEqualTo("Unhealthy");
        var association = Check(dto, "association");
        await Assert.That(association.Status).IsEqualTo("Violation");
        await Assert.That(association.Code).IsEqualTo("association-missing");
        await Assert.That(association.Message!).Contains("mesh-adapter");
        await Assert.That(association.Message!).Contains("reconcile");
        // The unlinked-but-intact account is still fully evaluated.
        await Assert.That(Check(dto, "configuration").Status).IsEqualTo("Healthy");
        await Assert.That(Check(dto, "client").Status).IsEqualTo("Healthy");
    }

    [Test]
    public async Task GetAdapterHealth_NoConfigurationAtAll_ReportsBothViolationsAndSkipsTheRest()
    {
        var sut = CreateSut();
        var adapter = RtEntityCreator.CreateAdapter();
        _resolver.GetAdapterDefaultAsync(TenantId, adapter.RtId)
            .Returns((RtServiceAccountConfiguration?)null);
        _repo.GetServiceAccountByWellKnownNameAsync(TenantId, Arg.Any<string>())
            .Returns((RtServiceAccountConfiguration?)null);

        var dto = await sut.GetAdapterHealthAsync(TenantId, adapter);

        using var _ = Assert.Multiple();
        await Assert.That(dto.OverallStatus).IsEqualTo("Unhealthy");
        await Assert.That(dto.ConfigurationRtId).IsNull();
        await Assert.That(dto.ClientId).IsNull();
        await Assert.That(Check(dto, "association").Code).IsEqualTo("association-missing");
        await Assert.That(Check(dto, "configuration").Code).IsEqualTo("configuration-missing");
        // Nothing downstream is evaluated (and identity is never asked) without an entity.
        await Assert.That(Check(dto, "client").Status).IsEqualTo("NotApplicable");
        await Assert.That(Check(dto, "secret").Status).IsEqualTo("NotApplicable");
        await Assert.That(Check(dto, "roles").Status).IsEqualTo("NotApplicable");
        await Assert.That(Check(dto, "delegation").Status).IsEqualTo("NotApplicable");
        await Assert.That(Check(dto, "tenant").Status).IsEqualTo("NotApplicable");
        await Assert.That(Check(dto, "issuerUri").Status).IsEqualTo("NotApplicable");
        await _identityReader.DidNotReceiveWithAnyArgs()
            .GetClientAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>());
    }

    // ---------------------------------------------------------------- client

    [Test]
    public async Task GetConfigurationHealth_ClientDoesNotExist_ReportsClientMissing()
    {
        var sut = CreateSut();
        var configuration = CreateHealthyConfiguration();
        _identityReader.GetClientAsync(TenantId, "client-id", Arg.Any<bool>())
            .Returns(IdentityClientLookup.NotFound);

        var dto = await sut.GetConfigurationHealthAsync(TenantId, configuration);

        using var _ = Assert.Multiple();
        await Assert.That(dto.OverallStatus).IsEqualTo("Unhealthy");
        var client = Check(dto, "client");
        await Assert.That(client.Status).IsEqualTo("Violation");
        await Assert.That(client.Code).IsEqualTo("client-missing");
        await Assert.That(client.Message!).Contains("client-id");
        await Assert.That(client.Message!).Contains("reconcile");
        // No client → no role edges / grants to compare against.
        await Assert.That(Check(dto, "roles").Status).IsEqualTo("NotApplicable");
        await Assert.That(Check(dto, "delegation").Status).IsEqualTo("NotApplicable");
        // The local checks still ran.
        await Assert.That(Check(dto, "secret").Status).IsEqualTo("Healthy");
    }

    // ---------------------------------------------------------------- secret

    [Test]
    public async Task GetConfigurationHealth_SecretMissing_ReportsSecretMissingWithoutTheValue()
    {
        var sut = CreateSut();
        // Built by hand: ClientSecret is deliberately never written (the mandatory-attribute
        // getter would throw; the health service reads via GetAttributeValueOrDefault).
        var configuration = new RtServiceAccountConfiguration
        {
            RtId = OctoObjectId.GenerateNewId(),
            CkTypeId = SystemCommunicationCkIds.RtCkServiceAccountConfigurationTypeId,
            RtWellKnownName = "secretless-account",
            ClientId = "client-id",
            IssuerUri = "{{service.authority}}",
            TenantId = TenantId,
            AssignedRoleNames = new AttributeStringValueList([CommonConstants.CommunicationManagementRole]),
            AllowDelegation = true
        };
        ArrangeMatchingIdentityClient();

        var dto = await sut.GetConfigurationHealthAsync(TenantId, configuration);

        using var _ = Assert.Multiple();
        await Assert.That(dto.OverallStatus).IsEqualTo("Unhealthy");
        var secret = Check(dto, "secret");
        await Assert.That(secret.Status).IsEqualTo("Violation");
        await Assert.That(secret.Code).IsEqualTo("secret-missing");
        await Assert.That(secret.Message!).Contains("rotate");
        // Nothing links this standalone account to an adapter, so no impersonation verdict either.
        await Assert.That(Check(dto, "impersonation").Status).IsEqualTo("NotApplicable");
    }

    // ---------------------------------------------------------------- roles

    [Test]
    public async Task GetConfigurationHealth_RolesDrift_ReportsMissingAndSuperfluousLists()
    {
        var sut = CreateSut();
        var configuration = CreateHealthyConfiguration();
        configuration.AssignedRoleNames = new AttributeStringValueList(["RoleA", "RoleB"]);
        _identityReader.GetClientAsync(TenantId, "client-id", true)
            .Returns(new IdentityClientLookup(IdentityClientLookupStatus.Found,
                new ClientDto
                {
                    ClientId = "client-id",
                    AllowedGrantTypes = ["client_credentials", LocalConstants.OnBehalfOfGrantType]
                },
                ["RoleB", "RoleC"], null));

        var dto = await sut.GetConfigurationHealthAsync(TenantId, configuration);

        using var _ = Assert.Multiple();
        await Assert.That(dto.OverallStatus).IsEqualTo("Unhealthy");
        var roles = Check(dto, "roles");
        await Assert.That(roles.Status).IsEqualTo("Violation");
        await Assert.That(roles.Code).IsEqualTo("roles-drift");
        await Assert.That(roles.MissingRoles!).IsEquivalentTo(new[] { "RoleA" });
        await Assert.That(roles.SuperfluousRoles!).IsEquivalentTo(new[] { "RoleC" });
        await Assert.That(roles.Message!).Contains("'RoleA'");
        await Assert.That(roles.Message!).Contains("'RoleC'");
    }

    [Test]
    public async Task GetConfigurationHealth_LegacyAccountWithoutDeclaration_RolesAreNotApplicable()
    {
        var sut = CreateSut();
        var configuration = RtEntityCreator.CreateServiceAccountConfiguration();
        configuration.IssuerUri = "{{service.authority}}";
        // No AssignedRoleNames, no AllowDelegation — a pre-3.32.0 entity.
        _identityReader.GetClientAsync(TenantId, "client-id", Arg.Any<bool>())
            .Returns(new IdentityClientLookup(IdentityClientLookupStatus.Found,
                new ClientDto
                {
                    ClientId = "client-id",
                    AllowedGrantTypes = ["client_credentials", LocalConstants.OnBehalfOfGrantType]
                },
                null, null));

        var dto = await sut.GetConfigurationHealthAsync(TenantId, configuration);

        using var _ = Assert.Multiple();
        // Whatever roles the client carries, a legacy account is deliberately unmanaged — never a
        // violation, and never even asked for (no role round trips).
        await Assert.That(dto.OverallStatus).IsEqualTo("Healthy");
        await Assert.That(Check(dto, "roles").Status).IsEqualTo("NotApplicable");
        // Legacy default AllowDelegation = true, and the client carries the grant → healthy.
        await Assert.That(Check(dto, "delegation").Status).IsEqualTo("Healthy");
        await _identityReader.Received(1).GetClientAsync(TenantId, "client-id", false);
    }

    // ---------------------------------------------------------------- delegation

    [Test]
    public async Task GetConfigurationHealth_DelegationDeclaredButGrantMissing_ReportsDrift()
    {
        var sut = CreateSut();
        var configuration = CreateHealthyConfiguration();
        _identityReader.GetClientAsync(TenantId, "client-id", true)
            .Returns(new IdentityClientLookup(IdentityClientLookupStatus.Found,
                new ClientDto { ClientId = "client-id", AllowedGrantTypes = ["client_credentials"] },
                [CommonConstants.CommunicationManagementRole], null));

        var dto = await sut.GetConfigurationHealthAsync(TenantId, configuration);

        using var _ = Assert.Multiple();
        await Assert.That(dto.OverallStatus).IsEqualTo("Unhealthy");
        var delegation = Check(dto, "delegation");
        await Assert.That(delegation.Status).IsEqualTo("Violation");
        await Assert.That(delegation.Code).IsEqualTo("delegation-drift");
        await Assert.That(delegation.Message!).Contains("lacks the on-behalf-of grant");
    }

    [Test]
    public async Task GetConfigurationHealth_DelegationForbiddenButGrantPresent_ReportsDrift()
    {
        var sut = CreateSut();
        var configuration = CreateHealthyConfiguration();
        configuration.AllowDelegation = false;
        ArrangeMatchingIdentityClient();

        var dto = await sut.GetConfigurationHealthAsync(TenantId, configuration);

        using var _ = Assert.Multiple();
        var delegation = Check(dto, "delegation");
        await Assert.That(delegation.Status).IsEqualTo("Violation");
        await Assert.That(delegation.Code).IsEqualTo("delegation-drift");
        await Assert.That(delegation.Message!).Contains("still carries the on-behalf-of");
    }

    // ---------------------------------------------------------------- tenant

    [Test]
    public async Task GetConfigurationHealth_TenantMismatch_NamesTheForeignTenant()
    {
        var sut = CreateSut();
        var configuration = CreateHealthyConfiguration();
        configuration.TenantId = "energyiq";
        ArrangeMatchingIdentityClient();

        var dto = await sut.GetConfigurationHealthAsync(TenantId, configuration);

        using var _ = Assert.Multiple();
        await Assert.That(dto.OverallStatus).IsEqualTo("Unhealthy");
        var tenant = Check(dto, "tenant");
        await Assert.That(tenant.Status).IsEqualTo("Violation");
        await Assert.That(tenant.Code).IsEqualTo("tenant-mismatch");
        await Assert.That(tenant.Message!).Contains("points at tenant 'energyiq'");
    }

    // ---------------------------------------------------------------- issuerUri

    [Test]
    public async Task GetConfigurationHealth_IssuerUri_TokenIsCaseInsensitiveAndAuthorityIsAccepted()
    {
        var sut = CreateSut();
        ArrangeMatchingIdentityClient();

        // Same tolerance as the convergence sweep (shared helper): the token in any case, or the
        // installation's own authority (pre-AB#5111 entities).
        // The trailing-slash spelling counts as the same authority — seeds write the slash,
        // options usually don't, and neither is drift.
        foreach (var healthyIssuer in new[]
                 {
                     "{{service.authority}}", "{{ SERVICE.Authority }}", _options.AuthorityUrl,
                     _options.AuthorityUrl.TrimEnd('/') + "/"
                 })
        {
            var configuration = CreateHealthyConfiguration();
            configuration.IssuerUri = healthyIssuer;

            var dto = await sut.GetConfigurationHealthAsync(TenantId, configuration);

            await Assert.That(Check(dto, "issuerUri").Status).IsEqualTo("Healthy");
        }
    }

    [Test]
    public async Task GetConfigurationHealth_ForeignIssuer_IsAHealthyDeliberateTarget()
    {
        // AB#5115: a concrete URL that is not this installation stopped being "drift" — it is a
        // deliberate foreign identity target the reconcile leaves alone, and health agrees.
        var sut = CreateSut();
        var configuration = CreateHealthyConfiguration();
        configuration.IssuerUri = "https://identity.of-some-other-cluster.example.com";
        ArrangeMatchingIdentityClient();

        var dto = await sut.GetConfigurationHealthAsync(TenantId, configuration);

        using var _ = Assert.Multiple();
        await Assert.That(dto.OverallStatus).IsEqualTo("Healthy");
        var issuer = Check(dto, "issuerUri");
        await Assert.That(issuer.Status).IsEqualTo("Healthy");
        await Assert.That(issuer.Message!).Contains("of-some-other-cluster");
        await Assert.That(issuer.Message!).Contains("foreign");
    }

    [Test]
    public async Task GetConfigurationHealth_EmptyIssuerAndTenant_AreTheHealthyInstallationDefault()
    {
        var sut = CreateSut();
        var configuration = CreateInstallationDefaultConfiguration();
        ArrangeMatchingIdentityClient();

        var dto = await sut.GetConfigurationHealthAsync(TenantId, configuration);

        using var _ = Assert.Multiple();
        await Assert.That(dto.OverallStatus).IsEqualTo("Healthy");
        await Assert.That(Check(dto, "issuerUri").Status).IsEqualTo("Healthy");
        await Assert.That(Check(dto, "issuerUri").Message!).Contains("installation default");
        await Assert.That(Check(dto, "tenant").Status).IsEqualTo("Healthy");
        await Assert.That(Check(dto, "tenant").Message!).Contains("installation default");
    }

    [Test]
    public async Task GetConfigurationHealth_ForeignTenantWithForeignIssuer_IsAHealthyPairing()
    {
        var sut = CreateSut();
        var configuration = CreateHealthyConfiguration();
        configuration.IssuerUri = "https://identity.of-some-other-cluster.example.com";
        configuration.TenantId = "energyiq";
        ArrangeMatchingIdentityClient();

        var dto = await sut.GetConfigurationHealthAsync(TenantId, configuration);

        using var _ = Assert.Multiple();
        // The foreign tenant belongs to the foreign issuer — a deliberate pairing, not a mismatch.
        await Assert.That(dto.OverallStatus).IsEqualTo("Healthy");
        await Assert.That(Check(dto, "tenant").Status).IsEqualTo("Healthy");
        await Assert.That(Check(dto, "tenant").Message!).Contains("energyiq");
    }

    // ---------------------------------------------------------------- AB#5114 impersonation

    /// <summary>
    /// Arranges the AB#5114 secretless-standalone shape: a pipeline links the account and its
    /// adapter has an own client ('octo-pipeline-sa-adapter-own') with a usable secret.
    /// </summary>
    private void ArrangeCapableAdapterActor(RtServiceAccountConfiguration configuration)
    {
        var adapter = RtEntityCreator.CreateAdapter();
        var adapterOwn = RtEntityCreator.CreateServiceAccountConfiguration("adapter-own-account");
        adapterOwn.ClientId = "octo-pipeline-sa-adapter-own";
        var pipeline = RtEntityCreator.CreatePipeline();
        _repo.GetPipelinesUsingServiceAccountAsync(TenantId, configuration.RtId).Returns([pipeline]);
        _repo.GetAdapterByPipelineAsync(TenantId, pipeline.ToRtEntityId()).Returns(adapter);
        _resolver.GetAdapterDefaultAsync(TenantId, adapter.RtId).Returns(adapterOwn);
    }

    [Test]
    public async Task GetConfigurationHealth_SecretlessStandaloneWithEdge_SecretAndImpersonationHealthy()
    {
        var sut = CreateSut();
        var configuration = CreateInstallationDefaultConfiguration(withSecret: false);
        ArrangeMatchingIdentityClient();
        ArrangeCapableAdapterActor(configuration);
        // Identity's actors read surface (AB#5114) lists the adapter's own client as an actor.
        _identityReader.GetActorClientIdsAsync(TenantId, "client-id")
            .Returns(IdentityClientActorsLookup.Found(["octo-pipeline-sa-adapter-own"]));

        var dto = await sut.GetConfigurationHealthAsync(TenantId, configuration);

        using var _ = Assert.Multiple();
        // No secret is NOT a violation here — the account is used via impersonation (AB#5114).
        var secret = Check(dto, "secret");
        await Assert.That(secret.Status).IsEqualTo("Healthy");
        await Assert.That(secret.Message!).Contains("octo-pipeline-sa-adapter-own");
        // And the MayActAs edge is now verified through the actors read surface — fully green.
        var impersonation = Check(dto, "impersonation");
        await Assert.That(impersonation.Status).IsEqualTo("Healthy");
        await Assert.That(impersonation.Message!).Contains("MayActAs");
        await Assert.That(impersonation.Message!).Contains("octo-pipeline-sa-adapter-own");
        await Assert.That(dto.OverallStatus).IsEqualTo("Healthy");
    }

    [Test]
    public async Task GetConfigurationHealth_SecretlessStandaloneWithoutEdge_ImpersonationViolation()
    {
        var sut = CreateSut();
        var configuration = CreateInstallationDefaultConfiguration(withSecret: false);
        ArrangeMatchingIdentityClient();
        ArrangeCapableAdapterActor(configuration);
        // Identity answers authoritatively: the account has actors — but not this one.
        _identityReader.GetActorClientIdsAsync(TenantId, "client-id")
            .Returns(IdentityClientActorsLookup.Found(["some-other-actor"]));

        var dto = await sut.GetConfigurationHealthAsync(TenantId, configuration);

        using var _ = Assert.Multiple();
        await Assert.That(dto.OverallStatus).IsEqualTo("Unhealthy");
        // The secret check still trusts the actor's existence — the broken half is the edge.
        await Assert.That(Check(dto, "secret").Status).IsEqualTo("Healthy");
        var impersonation = Check(dto, "impersonation");
        await Assert.That(impersonation.Status).IsEqualTo("Violation");
        await Assert.That(impersonation.Code).IsEqualTo("impersonation-edge-missing");
        await Assert.That(impersonation.Message!).Contains("MayActAs");
        await Assert.That(impersonation.Message!).Contains("octo-pipeline-sa-adapter-own");
        await Assert.That(impersonation.Message!).Contains("reconcile");
    }

    [Test]
    public async Task GetConfigurationHealth_SecretlessStandaloneEmptyActorList_ImpersonationViolation()
    {
        var sut = CreateSut();
        var configuration = CreateInstallationDefaultConfiguration(withSecret: false);
        ArrangeMatchingIdentityClient();
        ArrangeCapableAdapterActor(configuration);
        // An authoritative empty list ("client exists, nobody may act for it") is drift too.
        _identityReader.GetActorClientIdsAsync(TenantId, "client-id")
            .Returns(IdentityClientActorsLookup.Found([]));

        var dto = await sut.GetConfigurationHealthAsync(TenantId, configuration);

        using var _ = Assert.Multiple();
        await Assert.That(dto.OverallStatus).IsEqualTo("Unhealthy");
        await Assert.That(Check(dto, "impersonation").Status).IsEqualTo("Violation");
        await Assert.That(Check(dto, "impersonation").Code).IsEqualTo("impersonation-edge-missing");
    }

    [Test]
    public async Task GetConfigurationHealth_SecretlessStandaloneActorsUnreachable_ImpersonationUnknown()
    {
        var sut = CreateSut();
        var configuration = CreateInstallationDefaultConfiguration(withSecret: false);
        ArrangeMatchingIdentityClient();
        ArrangeCapableAdapterActor(configuration);
        _identityReader.GetActorClientIdsAsync(TenantId, "client-id")
            .Returns(IdentityClientActorsLookup.Unavailable(
                "the identity service could not be queried: connection refused"));

        var dto = await sut.GetConfigurationHealthAsync(TenantId, configuration);

        using var _ = Assert.Multiple();
        // Degrade, don't accuse: unreachable identity is Unknown, never a violation.
        await Assert.That(dto.OverallStatus).IsEqualTo("Unknown");
        var impersonation = Check(dto, "impersonation");
        await Assert.That(impersonation.Status).IsEqualTo("Unknown");
        await Assert.That(impersonation.Code).IsNull();
        await Assert.That(impersonation.Message!).Contains("connection refused");
    }

    [Test]
    public async Task GetConfigurationHealth_SecretlessStandaloneTargetClientGone_ImpersonationViolation()
    {
        var sut = CreateSut();
        var configuration = CreateInstallationDefaultConfiguration(withSecret: false);
        ArrangeCapableAdapterActor(configuration);
        // Identity knows neither the client nor (therefore) any edge onto it — both the client
        // check and the impersonation check carry their own violation.
        _identityReader.GetClientAsync(TenantId, "client-id", Arg.Any<bool>())
            .Returns(IdentityClientLookup.NotFound);
        _identityReader.GetActorClientIdsAsync(TenantId, "client-id")
            .Returns(IdentityClientActorsLookup.NotFound);

        var dto = await sut.GetConfigurationHealthAsync(TenantId, configuration);

        using var _ = Assert.Multiple();
        await Assert.That(dto.OverallStatus).IsEqualTo("Unhealthy");
        await Assert.That(Check(dto, "client").Code).IsEqualTo("client-missing");
        var impersonation = Check(dto, "impersonation");
        await Assert.That(impersonation.Status).IsEqualTo("Violation");
        await Assert.That(impersonation.Code).IsEqualTo("impersonation-edge-missing");
        await Assert.That(impersonation.Message!).Contains("does not exist");
    }

    [Test]
    public async Task GetConfigurationHealth_SecretlessStandaloneWithIncapableAdapter_SecretViolation()
    {
        var sut = CreateSut();
        var configuration = CreateInstallationDefaultConfiguration(withSecret: false);
        ArrangeMatchingIdentityClient();

        // The using adapter exists but has no own account — impersonation is impossible.
        var adapter = RtEntityCreator.CreateAdapter();
        var pipeline = RtEntityCreator.CreatePipeline();
        _repo.GetPipelinesUsingServiceAccountAsync(TenantId, configuration.RtId).Returns([pipeline]);
        _repo.GetAdapterByPipelineAsync(TenantId, pipeline.ToRtEntityId()).Returns(adapter);
        _resolver.GetAdapterDefaultAsync(TenantId, adapter.RtId)
            .Returns((RtServiceAccountConfiguration?)null);

        var dto = await sut.GetConfigurationHealthAsync(TenantId, configuration);

        using var _ = Assert.Multiple();
        await Assert.That(dto.OverallStatus).IsEqualTo("Unhealthy");
        await Assert.That(Check(dto, "secret").Status).IsEqualTo("Violation");
        await Assert.That(Check(dto, "secret").Code).IsEqualTo("secret-missing");
        await Assert.That(Check(dto, "impersonation").Status).IsEqualTo("NotApplicable");
    }

    [Test]
    public async Task GetAdapterHealth_SecretlessDefaultAccount_IsAlwaysASecretViolation()
    {
        // The adapter variant evaluates the adapter's OWN account (AB#5072): it cannot impersonate
        // itself, so a missing secret leaves the adapter with no credentials at all.
        var sut = CreateSut();
        var adapter = RtEntityCreator.CreateAdapter();
        var configuration = CreateInstallationDefaultConfiguration(withSecret: false);
        _resolver.GetAdapterDefaultAsync(TenantId, adapter.RtId).Returns(configuration);
        ArrangeMatchingIdentityClient();

        var dto = await sut.GetAdapterHealthAsync(TenantId, adapter);

        using var _ = Assert.Multiple();
        await Assert.That(dto.OverallStatus).IsEqualTo("Unhealthy");
        await Assert.That(Check(dto, "secret").Status).IsEqualTo("Violation");
        await Assert.That(Check(dto, "impersonation").Status).IsEqualTo("NotApplicable");
        await Assert.That(Check(dto, "impersonation").Message!).Contains("own client");
    }

    [Test]
    public async Task GetConfigurationHealth_PlaceholderSecret_CountsAsNoSecret()
    {
        var sut = CreateSut();
        var configuration = CreateHealthyConfiguration();
        configuration.ClientSecret = "<insert secret here>";
        ArrangeMatchingIdentityClient();

        var dto = await sut.GetConfigurationHealthAsync(TenantId, configuration);

        using var _ = Assert.Multiple();
        // Same rule as the adapter-side token service: an angle-bracket placeholder is no secret.
        await Assert.That(Check(dto, "secret").Status).IsEqualTo("Violation");
        await Assert.That(Check(dto, "secret").Code).IsEqualTo("secret-missing");
    }

    // ---------------------------------------------------------------- identity unreachable

    [Test]
    public async Task GetConfigurationHealth_IdentityUnreachable_IdentityChecksUnknownCallSucceeds()
    {
        var sut = CreateSut();
        var configuration = CreateHealthyConfiguration();
        _identityReader.GetClientAsync(TenantId, "client-id", Arg.Any<bool>())
            .Returns(IdentityClientLookup.Unavailable("the identity service could not be queried: connection refused"));

        var dto = await sut.GetConfigurationHealthAsync(TenantId, configuration);

        using var _ = Assert.Multiple();
        // Degrade, don't die: no violation anywhere, but the identity-backed checks are honest
        // about not knowing — and the overall status says so.
        await Assert.That(dto.OverallStatus).IsEqualTo("Unknown");
        await Assert.That(Check(dto, "client").Status).IsEqualTo("Unknown");
        await Assert.That(Check(dto, "client").Message!).Contains("connection refused");
        await Assert.That(Check(dto, "roles").Status).IsEqualTo("Unknown");
        await Assert.That(Check(dto, "delegation").Status).IsEqualTo("Unknown");
        // The local checks still answer authoritatively.
        await Assert.That(Check(dto, "secret").Status).IsEqualTo("Healthy");
        await Assert.That(Check(dto, "tenant").Status).IsEqualTo("Healthy");
        await Assert.That(Check(dto, "issuerUri").Status).IsEqualTo("Healthy");
    }

    [Test]
    public async Task GetConfigurationHealth_IdentityFoundButRolesUnreadable_RolesUnknown()
    {
        var sut = CreateSut();
        var configuration = CreateHealthyConfiguration();
        _identityReader.GetClientAsync(TenantId, "client-id", true)
            .Returns(new IdentityClientLookup(IdentityClientLookupStatus.Found,
                new ClientDto
                {
                    ClientId = "client-id",
                    AllowedGrantTypes = ["client_credentials", LocalConstants.OnBehalfOfGrantType]
                },
                null, null)); // partial degradation: client read fine, role reads failed

        var dto = await sut.GetConfigurationHealthAsync(TenantId, configuration);

        using var _ = Assert.Multiple();
        await Assert.That(dto.OverallStatus).IsEqualTo("Unknown");
        await Assert.That(Check(dto, "client").Status).IsEqualTo("Healthy");
        await Assert.That(Check(dto, "roles").Status).IsEqualTo("Unknown");
        await Assert.That(Check(dto, "delegation").Status).IsEqualTo("Healthy");
    }
}
