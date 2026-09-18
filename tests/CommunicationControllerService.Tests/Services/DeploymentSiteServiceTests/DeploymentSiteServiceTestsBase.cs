using System.Diagnostics.CodeAnalysis;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.DeploymentSites;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Hubs;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Communication.Contracts.Hubs;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.DeploymentSiteServiceTests;

internal abstract class PoolServiceTestsBase
{
    protected const string TenantId = "tenantId";
    protected const string DeploymentSiteName = "default";
    protected const string ConnectionId = "connectionId";
    protected static readonly OctoObjectId DeploymentSiteRtId = OctoObjectId.GenerateNewId();

    protected readonly ICommunicationRepository CommunicationRepository;
    protected readonly IDeploymentSiteCache DeploymentSiteCache;
    protected readonly ICommunicationEventService CommunicationEventService;
    protected readonly IOperatorConnectionManager OperatorConnectionManager;
    protected readonly IWorkloadEncryptionService EncryptionService;
    protected readonly IWorkloadTemplateResolver TemplateResolver;
    protected readonly IWorkloadOnDemandCapabilityService OnDemandCapabilityService;
    protected readonly IPipelineServiceAccountProvisioningService ServiceAccountProvisioningService;
    protected readonly IPipelineServiceAccountResolver ServiceAccountResolver;
    /// <summary>AB#4924 — substituted; the real walk is covered by TenantLendingScopeResolverTests.</summary>
    protected readonly ITenantLendingScopeResolver LendingScopeResolver = Substitute.For<ITenantLendingScopeResolver>();

    /// <summary>AB#4924 — substituted; the scale verb itself is covered by WorkloadLifecycleServiceTests.</summary>
    protected readonly IWorkloadLifecycleService WorkloadLifecycleService =
        Substitute.For<IWorkloadLifecycleService>();

    /// <summary>AB#5271 — substituted; the fan-out itself is covered by
    ///     AdapterPoolMirrorProvisioningServiceTests. What this suite asserts is that a pool deploy
    ///     triggers it and an adapter deploy does not.</summary>
    protected readonly IAdapterPoolMirrorProvisioningService AdapterPoolMirrorProvisioningService =
        Substitute.For<IAdapterPoolMirrorProvisioningService>();
    protected readonly IDeploymentSiteCachePublish PoolCachePublish;
    protected readonly DeploymentSiteTenant DeploymentSiteTenant;
    protected readonly DeploymentSiteService DeploymentSiteService;

    [SuppressMessage("Substitute creation", "NS2002:Constructor parameters count mismatch.")]
    [SuppressMessage("Argument matchers", "NS3003:Multiple matchers of same type",
        Justification = "TryResolve has three string? out params (resolved + unknownPlaceholder); the matchers are unambiguous by position.")]
    protected PoolServiceTestsBase()
    {
        CommunicationRepository = Substitute.For<ICommunicationRepository>();
        DeploymentSiteCache = Substitute.For<IDeploymentSiteCache>();
        CommunicationEventService = Substitute.For<ICommunicationEventService>();
        OperatorConnectionManager = Substitute.For<IOperatorConnectionManager>();
        // Default: no other operator connection is still claiming any deploymentSite.
        // The multi-claim guard in SetCommunicationStateOfflineAsync needs a
        // non-null IReadOnlyList<string> back from this call; tests that
        // exercise the multi-claim path override the return value.
        OperatorConnectionManager
            .GetConnectionsForDeploymentSite(Arg.Any<string>(), Arg.Any<string>())
            .Returns(Array.Empty<string>());
        EncryptionService = Substitute.For<IWorkloadEncryptionService>();
        // Default: Decrypt passes the value through unchanged (so non-secret
        // tests don't have to set up Decrypt). Specific tests override this.
        EncryptionService.Decrypt(Arg.Any<string>()).Returns(ci => ci.Arg<string>());
        TemplateResolver = Substitute.For<IWorkloadTemplateResolver>();
        // Default: literal pass-through (no placeholders configured). Tests
        // that exercise template behaviour override TryResolve explicitly.
        TemplateResolver.AvailableDomains
            .Returns(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        TemplateResolver.AvailableServiceUrls
            .Returns(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        TemplateResolver
            .TryResolve(Arg.Any<string?>(), Arg.Any<WorkloadTemplateContext>(),
                out Arg.Any<string?>(), out Arg.Any<string?>())
            .Returns(ci =>
            {
                // Default behaviour: pass the input through unchanged so tests
                // that don't touch the resolver see literal values. Use
                // positional ci.ArgAt<T> for the two out params (positions 2
                // and 3 after template + context).
                ci[2] = ci.ArgAt<string?>(0);
                ci[3] = (string?)null;
                return true;
            });
        OnDemandCapabilityService = Substitute.For<IWorkloadOnDemandCapabilityService>();
        // Default: every workload is on-demand capable (AB#4984). Tests that exercise
        // the capability rejection override EvaluateAsync explicitly.
        OnDemandCapabilityService
            .EvaluateAsync(Arg.Any<string>(), Arg.Any<RtEntityId>())
            .Returns(new OnDemandCapabilityResult(true, []));
        PoolCachePublish = Substitute.For<IDeploymentSiteCachePublish>();
        DeploymentSiteTenant = new DeploymentSiteTenant(PoolCachePublish, TenantId);

        // AB#5027: the deploy path provisions the adapter's pipeline service account. Substituted
        // here — the real behaviour is covered by PipelineServiceAccountProvisioningServiceTests;
        // what the deploymentSite suite asserts is that the call is made for Adapters and only for Adapters.
        ServiceAccountProvisioningService = Substitute.For<IPipelineServiceAccountProvisioningService>();

        // AB#5072: the deploy path projects the adapter's provisioned credentials into the
        // workload's Helm values. Default: nothing linked, so the pre-AB#5072 DTO shape is what
        // every existing suite keeps seeing. Tests that exercise the projection arrange an account.
        ServiceAccountResolver = Substitute.For<IPipelineServiceAccountResolver>();
        ServiceAccountResolver
            .GetAdapterDefaultAsync(Arg.Any<string>(), Arg.Any<OctoObjectId>())
            .Returns((RtServiceAccountConfiguration?)null);

        DeploymentSiteService = new DeploymentSiteService(
            CommunicationRepository,
            DeploymentSiteCache,
            CommunicationEventService,
            OperatorConnectionManager,
            EncryptionService,
            TemplateResolver,
            OnDemandCapabilityService,
            ServiceAccountProvisioningService,
            ServiceAccountResolver,
            LendingScopeResolver,
            WorkloadLifecycleService,
            AdapterPoolMirrorProvisioningService);
    }

    [SuppressMessage("Non-substitutable member", "NS1004:Argument matcher used with a non-virtual member of a class.")]
    protected void GivenTenantInCache()
    {
        DeploymentSiteCache.TryGetTenant(TenantId, out Arg.Any<DeploymentSiteTenant?>())
            .Returns(x =>
            {
                x[1] = DeploymentSiteTenant;
                return true;
            });
    }

    [SuppressMessage("Non-substitutable member", "NS1004:Argument matcher used with a non-virtual member of a class.")]
    protected void GivenTenantNotInCache()
    {
        DeploymentSiteCache.TryGetTenant(TenantId, out Arg.Any<DeploymentSiteTenant?>())
            .Returns(false);
    }

    protected DeploymentSite AddDeploymentSiteToTenant(string deploymentSiteName = DeploymentSiteName, string connectionId = ConnectionId)
    {
        return DeploymentSiteTenant.AddDeploymentSite(deploymentSiteName, DeploymentSiteRtId, connectionId);
    }
}
