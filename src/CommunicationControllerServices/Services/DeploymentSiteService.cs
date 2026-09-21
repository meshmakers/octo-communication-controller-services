using System.Globalization;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.DeploymentSites;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Hubs;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Contracts.Hubs;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using Meshmakers.Octo.Runtime.Contracts;
using NLog;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

internal class DeploymentSiteService : IDeploymentSiteService
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private readonly ICommunicationRepository _communicationRepository;
    private readonly IDeploymentSiteCache _deploymentSiteCache;
    private readonly ICommunicationEventService _eventService;
    private readonly IOperatorConnectionManager _operatorConnectionManager;
    private readonly IWorkloadEncryptionService _encryptionService;
    private readonly IWorkloadTemplateResolver _templateResolver;
    private readonly IWorkloadOnDemandCapabilityService _onDemandCapabilityService;
    private readonly IPipelineServiceAccountProvisioningService _serviceAccountProvisioningService;
    private readonly IPipelineServiceAccountResolver _serviceAccountResolver;
    private readonly ITenantLendingScopeResolver _lendingScopeResolver;
    private readonly IWorkloadLifecycleService _workloadLifecycleService;
    private readonly IAdapterPoolMirrorProvisioningService _adapterPoolMirrorProvisioningService;

    /// <summary>
    /// Helm values path carrying the adapter's own OAuth client id (AB#5072). Must stay in lockstep
    /// with <c>octo-mesh-adapter/src/charts/octo-mesh-adapter/templates/_env.tpl</c>, which reads
    /// <c>.Values.serviceAccountClientId</c> into <c>OCTO_ADAPTER__CLIENTID</c>. This constant and
    /// that template are the only coupling between the two repositories, and a typo in either is
    /// invisible until the pod is running: the adapter would simply connect anonymously.
    /// </summary>
    internal const string ServiceAccountClientIdValuePath = "serviceAccountClientId";

    /// <summary>
    /// Helm values path carrying the adapter's own OAuth client secret (AB#5072), projected
    /// secret-flagged so the operator materialises it into <c>{release}-octo-secrets</c> and the
    /// chart receives a <c>valueFrom.secretKeyRef</c> map. Chart counterpart:
    /// <c>.Values.secrets.serviceAccountClientSecret</c> through <c>octo-mesh.secretEnv</c>, the same
    /// helper <c>secrets.rabbitmq</c> uses.
    /// </summary>
    internal const string ServiceAccountClientSecretValuePath = "secrets.serviceAccountClientSecret";

    /// <summary>
    /// Constructor
    /// </summary>
    /// <param name="communicationRepository">Communication repository</param>
    /// <param name="poolCache">Distributed and synchronized data between nodes</param>
    /// <param name="eventService">Service for storing system events</param>
    /// <param name="operatorConnectionManager">Manages SignalR connections to central Communication Operators (for Cloud-deploymentSite deploy/undeploy notifications and PreUpdateTenant fan-out)</param>
    /// <param name="encryptionService">Decrypts secret-flagged ValueOverride values before they go on the SignalR wire</param>
    /// <param name="templateResolver">Resolves <c>{{domain.NAME}}</c>, <c>{{service.NAME}}</c> and <c>{{context.tenantId}}</c> placeholders in workload <c>Hostname</c>, non-secret <c>ValueOverride.Value</c> and <c>ValuesYaml</c> at deploy time</param>
    /// <param name="onDemandCapabilityService">Validates LifecycleMode=OnDemand against the workload's trigger classification at deploy time (AB#4984)</param>
    /// <param name="serviceAccountProvisioningService">Provisions the adapter's pipeline service account on deploy (AB#5027)</param>
    /// <param name="serviceAccountResolver">Reads the adapter's provisioned service account so its credentials can be projected into the workload's Helm values (AB#5072)</param>
    /// <param name="lendingScopeResolver">Resolves which tenants an adapter deploymentSite may lend to, so a Leased workload naming an out-of-scope lender is refused at deploy time (AB#4924)</param>
    /// <param name="workloadLifecycleService">Carries the AB#4917 scale verb to the operator owning the workload's deploymentSite; reused for adapter-pool scaling so the MinReplicas floor is enforced in one place (AB#4924)</param>
    /// <param name="adapterPoolMirrorProvisioningService">Pushes an adapter pool's deploy/undeploy out to the LentAdapterPool mirrors its borrowers hold (AB#5271)</param>
    public DeploymentSiteService(ICommunicationRepository communicationRepository, IDeploymentSiteCache poolCache,
        ICommunicationEventService eventService,
        IOperatorConnectionManager operatorConnectionManager,
        IWorkloadEncryptionService encryptionService,
        IWorkloadTemplateResolver templateResolver,
        IWorkloadOnDemandCapabilityService onDemandCapabilityService,
        IPipelineServiceAccountProvisioningService serviceAccountProvisioningService,
        IPipelineServiceAccountResolver serviceAccountResolver,
        ITenantLendingScopeResolver lendingScopeResolver,
        IWorkloadLifecycleService workloadLifecycleService,
        IAdapterPoolMirrorProvisioningService adapterPoolMirrorProvisioningService)
    {
        _communicationRepository = communicationRepository;
        _deploymentSiteCache = poolCache;
        _eventService = eventService;
        _operatorConnectionManager = operatorConnectionManager;
        _encryptionService = encryptionService;
        _templateResolver = templateResolver;
        _onDemandCapabilityService = onDemandCapabilityService;
        _serviceAccountProvisioningService = serviceAccountProvisioningService;
        _serviceAccountResolver = serviceAccountResolver;
        _lendingScopeResolver = lendingScopeResolver;
        _workloadLifecycleService = workloadLifecycleService;
        _adapterPoolMirrorProvisioningService = adapterPoolMirrorProvisioningService;
    }
    
    /// <inheritdoc />
    public async Task UnregisterDeploymentSiteOperatorAsync(string tenantId, OctoObjectId deploymentSiteRtId)
    {
        Logger.Info("[{TenantId}] Unregistering operator for deploymentSite '{DeploymentSiteRtId}'",
            tenantId, deploymentSiteRtId);

        if (!_deploymentSiteCache.TryGetTenant(tenantId, out var tenantDescription))
        {
            return;
        }
        if (!tenantDescription.DeploymentSitesById.TryGetValue(deploymentSiteRtId, out var deploymentSiteDescription))
        {
            return;
        }

        // Set communication state to Unregistered *before* removing from cache.
        // After RemoveDeploymentSite, the OnDisconnectedAsync that follows the operator's
        // graceful disconnect can no longer locate the deploymentSite, so any state write
        // would silently no-op and the UI would keep showing Online forever.
        await _communicationRepository.SetDeploymentSiteCommunicationStateAsync(tenantId, deploymentSiteDescription.DeploymentSiteRtId,
            RtCommunicationStateEnum.Unregistered);

        var deploymentSiteName = deploymentSiteDescription.DeploymentSiteName;
        tenantDescription.RemoveDeploymentSite(deploymentSiteDescription.DeploymentSiteRtId);

        // Edge deploymentSites stay Disabled regardless of operator presence; only Cloud
        // deploymentSites flip back to Pending until a new operator re-registers.
        var deploymentSites = await _communicationRepository.GetDeploymentSitesAsync(tenantId);
        var rtDeploymentSite = deploymentSites.FirstOrDefault(p => p.RtId == deploymentSiteRtId);
        if (rtDeploymentSite != null && !ActiveDeployment.IsActive(rtDeploymentSite.DeploymentState))
        {
            // AB#4255: an operator releasing a deploymentSite that already rests (UndeployDeploymentSiteAsync wrote
            // Undeployed / Disabled before notifying it) is the acknowledgement of that undeploy.
            // Overwriting the resting state here parked every gracefully undeployed Cloud deploymentSite at
            // Pending forever, which the Communication disable guard would then refuse on.
            Logger.Info(
                "[{TenantId}] DeploymentSite '{DeploymentSiteRtId}' already rests at {DeploymentState}; operator release leaves it there",
                tenantId, deploymentSiteRtId, rtDeploymentSite.DeploymentState);
        }
        else
        {
            var targetState = rtDeploymentSite?.Environment == RtEnvironmentEnum.Edge
                ? RtDeploymentStateEnum.Disabled
                : RtDeploymentStateEnum.Pending;
            await _communicationRepository.SetDeploymentSiteDeploymentStateAsync(tenantId, deploymentSiteDescription.DeploymentSiteRtId,
                targetState);
        }

        await _eventService.StoreInformationEventAsync(tenantId,
            $"DeploymentSite operator for deploymentSite '{deploymentSiteName}' unregistered.",
            new RtEntityId(SystemCommunicationCkIds.RtCkDeploymentSiteTypeId, deploymentSiteDescription.DeploymentSiteRtId));

        Logger.Info("[{TenantId}] Operator for deploymentSite '{DeploymentSiteRtId}' unregistered", tenantId, deploymentSiteRtId);
    }

    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public async Task PreUpdateTenantAsync(string tenantId)
    {
        Logger.Info("[{TenantId}] PreUpdate tenant", tenantId);

        try
        {
            await _semaphore.WaitAsync();

            if (_deploymentSiteCache.TryGetTenant(tenantId, out var deploymentSiteTenant))
            {
                // Inform all connected operators that the tenant is about to
                // be updated. Replaces the per-deploymentSite /poolHub fan-out — every
                // operator multiplexes through its single /operatorHub channel.
                await _operatorConnectionManager.NotifyPreUpdateTenantAsync(tenantId);
                // Remove all deploymentSites from cache so we skip the possibility to
                // communicate with them while the CK-cache is unloaded.
                _deploymentSiteCache.RemoveTenant(tenantId);

                // Note: we do NOT touch CommunicationState in the database here.
                // The legacy /poolHub design had to mark every deploymentSite Unregistered
                // because the per-deploymentSite SignalR connection died on cache flush
                // and only re-registered after the operator reconnected. With
                // the new /operatorHub model the operator's connection survives
                // tenant cache reloads entirely — deploymentSites stay Online unless the
                // operator actually disconnects, in which case OnDisconnectedAsync
                // sets them Offline.

                await _eventService.StoreInformationEventAsync(tenantId,
                    $"Tenant pre-update completed. {deploymentSiteTenant.DeploymentSitesById.Count} deploymentSite(s) flushed from cache.");
            }
        }
        catch (Exception e)
        {
            throw DeploymentSiteServiceException.PreUpdateTenantFailed(tenantId, e);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task PosUpdateTenantAsync(string tenantId)
    {
        Logger.Info("[{TenantId}] PosUpdate tenant", tenantId);

        try
        {
            await _semaphore.WaitAsync();

            _deploymentSiteCache.AddOrUpdateTenant(tenantId);

            // Note: deploymentSite CommunicationState is intentionally NOT reset here.
            // See PreUpdateTenantAsync above for the full rationale — the
            // operator-hub model decouples connection lifecycle from tenant
            // cache lifecycle, so the on-disk state is authoritative and
            // should be preserved across cache reloads.

            await _eventService.StoreInformationEventAsync(tenantId,
                "Tenant post-update completed. DeploymentSite cache re-initialized.");
        }
        catch (Exception e)
        {
            throw DeploymentSiteServiceException.PosUpdateTenantFailed(tenantId, e);
        }
        finally
        {
            _semaphore.Release();
        }

        // Outside the semaphore: recompute DeploymentState across all deploymentSites /
        // workloads / pipelines / triggers. This is the catch-all backfill that
        // keeps the DB in sync with the Disabled rules whenever a tenant is
        // (re-)enabled or its CK model updated. Runs after PosUpdate so the
        // deploymentSite cache is already re-initialised.
        try
        {
            await RecomputeAllDeploymentStatesAsync(tenantId);
        }
        catch (Exception e)
        {
            // Backfill is best-effort — log but don't fail the PosUpdate handler.
            Logger.Warn(e,
                "[{TenantId}] DeploymentState recompute after PosUpdateTenant failed", tenantId);
        }
    }

    /// <inheritdoc />
    public async Task DeployDeploymentSiteAsync(string tenantId, OctoObjectId deploymentSiteRtId)
    {
        Logger.Info("[{TenantId}] Deploying deploymentSite '{DeploymentSiteRtId}'", tenantId, deploymentSiteRtId);

        var rtDeploymentSite = await GetPoolByRtIdAsync(tenantId, deploymentSiteRtId);

        if (rtDeploymentSite.Environment == RtEnvironmentEnum.Edge)
        {
            // We never ask the central operator to deploy an Edge deploymentSite. The
            // entity's DeploymentState is left untouched here — it reflects
            // whatever the operator last reported (e.g. Deployed if the deploymentSite
            // was Cloud-deployed before the user switched it to Edge; the
            // user must call Undeploy to clean those resources up). The
            // backfill takes care of moving Undeployed Edge deploymentSites to
            // Disabled separately.
            throw DeploymentSiteServiceException.EdgePoolNotDeployable(tenantId, deploymentSiteRtId, rtDeploymentSite.Name);
        }

        var deploymentSiteName = rtDeploymentSite.Name ?? string.Empty;
        Logger.Info(
            "[{TenantId}] DeploymentSite '{DeploymentSiteName}' (rtId {DeploymentSiteRtId}) is Cloud — notifying central Communication Operator",
            tenantId, deploymentSiteName, deploymentSiteRtId);
        await _operatorConnectionManager.NotifyDeploymentSiteDeployedAsync(new DeployedDeploymentSiteDto
        {
            TenantId = tenantId,
            DeploymentSiteRtId = deploymentSiteRtId.ToString(),
        });

        await _communicationRepository.SetDeploymentSiteDeploymentStateAsync(tenantId, deploymentSiteRtId,
            RtDeploymentStateEnum.Deployed);

        // Note: workloads are NOT auto-deployed here. Users (or callers)
        // trigger DeployWorkloadAsync per workload explicitly — this lets
        // the deploymentSite's CommunicationState turn Online first, so any issue
        // with the deploymentSite itself is visible before any helm install runs.
        // Use case: smoke-test a fresh deploymentSite, then phase adapter deploys.

        await _eventService.StoreInformationEventAsync(tenantId,
            $"DeploymentSite '{deploymentSiteName}' deployed.");
    }

    /// <inheritdoc />
    public async Task DeployWorkloadAsync(string tenantId, OctoObjectId workloadRtId,
        bool isReconciliation = false)
    {
        Logger.Info("[{TenantId}] Deploying workload '{WorkloadRtId}'", tenantId, workloadRtId);

        var workload = await _communicationRepository.GetWorkloadByRtIdAsync(tenantId, workloadRtId);
        if (workload == null)
        {
            throw DeploymentSiteServiceException.WorkloadNotFound(tenantId, workloadRtId);
        }

        var deploymentSite = await _communicationRepository.GetDeploymentSiteForWorkloadAsync(tenantId, workload.RtId);
        if (deploymentSite == null)
        {
            throw DeploymentSiteServiceException.WorkloadNotInDeploymentSite(tenantId, workloadRtId);
        }

        // Workloads in Edge deploymentSites are deployable: NotifyWorkloadDeployedAsync
        // routes via RegisterDeploymentSiteForConnection to whichever operator (central
        // or edge) registered the deploymentSite, and OperatorHubService.WorkloadDeployedAsync
        // runs the same helm upgrade --install path in either mode. Only the
        // deploymentSite itself (CR + broker secret) is central-cluster-only and rejected
        // in DeployDeploymentSiteAsync.

        // Validate the workload's Helm fields up-front so we can throw a precise
        // exception telling the user exactly what to fix. BuildWorkloadDeployedDtoAsync
        // intentionally returns null for any missing field (silently skipped by the
        // deploymentSite fan-out), but for an explicit user-triggered single-workload deploy
        // the user deserves to know which field is missing.
        await EnsureWorkloadIsHelmDeployableAsync(tenantId, workload);

        // AB#5027 phase 2: deploying an Adapter is the closest thing this service has to "an adapter
        // was created" — adapters themselves are RtEntities written through the asset repository, so
        // there is no create hook here, but nothing can run pipelines before its workload is
        // deployed. Provisioning the execution identity right here therefore closes the window
        // between an operator adding an adapter and the next tenant load, so the very first pipeline
        // deploy onto a brand-new adapter already passes the phase 1 guard.
        //
        // Best-effort by construction (EnsureAdapterProvisionedAsync never throws and writes its own
        // error event): a failure must not fail a deploy that is otherwise fine, and the tenant-load
        // sweep re-converges. Applications are skipped — they execute no pipelines.
        //
        // 🔴 AB#5072 MOVED THIS BEFORE THE DEPLOY NOTIFICATION. It used to run after it, so the
        // identity round trip could not delay the helm rollout. That was free while the account was
        // only ever read back by a *later* pipeline deploy — but the deploy notification now carries
        // the account's credentials into the workload's Helm values, and the DTO is built from what
        // exists at that moment. Provisioning afterwards means the very first deploy of a
        // freshly created adapter ships no credentials and the pod comes up anonymous; nothing
        // re-deploys it, so it stays that way until a human clicks Deploy a second time. That is not
        // an edge case — it is every new adapter, and once the adapter-hub gate (AB#5063) is armed
        // such a pod never gets online at all.
        //
        // The two rejected alternatives: firing a second deploy after provisioning costs a second
        // helm rollout and a second pod restart on every deploy and needs its own loop guard; and
        // documenting "the first deploy is anonymous" pushes a manual step onto every adapter
        // creation. The cost paid instead is latency on the deploy call, bounded by the
        // provisioning service's own 30 s identity timeout and normally a few milliseconds — and it
        // is paid before the rollout starts rather than during it, so a slow identity service delays
        // a deploy instead of half-configuring one.
        if (workload is RtAdapter adapterWorkload)
        {
            try
            {
                await _serviceAccountProvisioningService.EnsureAdapterProvisionedAsync(tenantId, adapterWorkload);
            }
            catch (Exception e)
            {
                // EnsureAdapterProvisionedAsync is contractually non-throwing and logs / audits its
                // own failures — but a deploy must not depend on that contract holding. Swallowing
                // it here degrades to exactly the pre-AB#5072 behaviour: the workload deploys, the
                // adapter connects anonymously, and the next deploy converges it.
                Logger.Error(e,
                    "[{TenantId}] Pipeline service account provisioning failed during the deploy of adapter '{WorkloadName}' ({WorkloadRtId}); the deploy continues without adapter credentials",
                    tenantId, workload.Name, workload.RtId);
            }
        }

        var deploymentSiteName = deploymentSite.Name ?? string.Empty;
        var dto = await BuildWorkloadDeployedDtoAsync(tenantId, deploymentSite.RtId, deploymentSiteName, workload, isReconciliation);
        if (dto == null)
        {
            // Should be unreachable after EnsureWorkloadIsHelmDeployableAsync, but
            // keep the fallback so the call can never silently no-op.
            throw DeploymentSiteServiceException.WorkloadMissingChartName(tenantId, workloadRtId, workload.Name);
        }

        await _operatorConnectionManager.NotifyWorkloadDeployedAsync(dto);

        // Set Pending immediately so a re-deploy is visible in the UI — e.g.
        // the user updates the chart version on a currently-Deployed adapter
        // and clicks Deploy: without this write the state would stay Deployed
        // throughout and the user would see no feedback that the helm-upgrade
        // actually ran. The operator's ReportWorkloadDeploymentStatusAsync
        // round-trip flips this to Deployed (success) or Error (failure)
        // within a few seconds.
        await SetWorkloadDeploymentStateAsync(tenantId, workload, RtDeploymentStateEnum.Pending);

        await _eventService.StoreInformationEventAsync(tenantId,
            $"Workload '{workload.Name}' deploy requested.");

        await FanOutAdapterPoolMirrorsAsync(tenantId, workload);
    }

    public async Task ReconcilePendingWorkloadsAsync(string tenantId, OctoObjectId deploymentSiteRtId)
    {
        // AB#4894: a deploy notification that raced an operator pod replacement is lost
        // silently, stranding the workload in Pending with nothing to reconcile it. On every
        // deploymentSite (re-)registration, re-dispatch whatever is still Pending. Best effort — this
        // runs on the registration path and must never fail it.
        IReadOnlyCollection<RtDeployableWorkload> workloads;
        try
        {
            workloads = await _communicationRepository.GetWorkloadsForDeploymentSiteAsync(tenantId, deploymentSiteRtId);
        }
        catch (Exception e)
        {
            // A tenant update may be unloading the CK cache concurrently (same race the
            // PreDeleteTenant cascade avoids via in-memory tracking) — skip this round, the
            // next registration reconciles.
            Logger.Warn(e,
                "[{TenantId}] Skipping pending-workload reconcile for deploymentSite {DeploymentSiteRtId}: workload lookup failed",
                tenantId, deploymentSiteRtId);
            return;
        }

        foreach (var workload in workloads.Where(w => w.DeploymentState == RtDeploymentStateEnum.Pending))
        {
            try
            {
                Logger.Info(
                    "[{TenantId}] Workload '{WorkloadName}' ({WorkloadRtId}) is stuck in Pending on deploymentSite registration — re-dispatching its deploy (AB#4894)",
                    tenantId, workload.Name, workload.RtId);

                // AB#4955: an empty ChartVersion means "newest in the repository", resolved by the
                // operator at `helm upgrade` time. This dispatch is not a release decision — it is
                // triggered by a deploymentSite re-registration, i.e. an operator restart, a blueprint
                // re-apply or a CK-model update — so an unpinned workload could come back on a
                // different version than it was running, with nobody having asked for it. That is
                // how the prod accounting fleet moved from 1.0.71 to 1.0.72 unattended. The DTO's
                // IsReconciliation flag now tells the operator to keep the installed version, but
                // an operator that pre-dates the flag still resolves anew — so the unpinned
                // re-dispatch stays worth surfacing either way.
                if (string.IsNullOrWhiteSpace(workload.ChartVersion))
                {
                    Logger.Warn(
                        "[{TenantId}] Workload '{WorkloadName}' ({WorkloadRtId}) has no pinned ChartVersion — an operator without AB#4955 support resolves a possibly newer chart on this unattended re-dispatch",
                        tenantId, workload.Name, workload.RtId);
                    await _eventService.StoreWarningEventAsync(tenantId,
                        $"Workload '{workload.Name}' was re-deployed automatically without a pinned chart version. A current operator keeps the chart version already installed; an older one resolves the newest chart in the repository. Pin ChartVersion to make deployments reproducible.");
                }
                else
                {
                    await _eventService.StoreInformationEventAsync(tenantId,
                        $"Workload '{workload.Name}' was still Pending when its deploymentSite re-registered — deploy re-dispatched.");
                }

                await DeployWorkloadAsync(tenantId, workload.RtId, isReconciliation: true);
            }
            catch (Exception e)
            {
                Logger.Warn(e,
                    "[{TenantId}] Re-dispatch of pending workload '{WorkloadName}' ({WorkloadRtId}) failed, continuing",
                    tenantId, workload.Name, workload.RtId);
            }
        }
    }

    private async Task SetWorkloadDeploymentStateAsync(string tenantId, RtDeployableWorkload workload,
        RtDeploymentStateEnum deploymentState)
    {
        switch (workload)
        {
            case RtAdapter:
                {
                    var rtEntityId = new RtEntityId(SystemCommunicationCkIds.RtCkAdapterTypeId, workload.RtId);
                    await _communicationRepository.SetAdapterDeploymentStateAsync(tenantId, rtEntityId, deploymentState);
                    break;
                }
            case RtApplication:
                {
                    var rtEntityId = new RtEntityId(SystemCommunicationCkIds.RtCkApplicationTypeId, workload.RtId);
                    await _communicationRepository.SetApplicationDeploymentStateAsync(tenantId, rtEntityId, deploymentState);
                    break;
                }
            // AB#4924: without this arm a deploymentSite's deploy left DeploymentState at its default, so the
            // UI never showed a deploymentSite as deployed and Undeploy refused it as "already not deployed"
            // — a workload that deploys and then cannot be taken down again.
            case RtAdapterPool:
                {
                    var rtEntityId = new RtEntityId(SystemCommunicationCkIds.RtCkAdapterPoolTypeId, workload.RtId);
                    await _communicationRepository.SetAdapterPoolDeploymentStateAsync(tenantId, rtEntityId,
                        deploymentState);
                    break;
                }
            default:
                // Defensive — if a new DeployableWorkload subtype is added without a
                // dedicated setter, we'd silently skip the write. Make that visible
                // in the log instead of looking like the write succeeded.
                Logger.Warn(
                    "[{TenantId}] No DeploymentState setter for workload of type '{Type}' (RtId '{RtId}'); skipping",
                    tenantId, workload.GetType().Name, workload.RtId);
                break;
        }
    }

    /// <summary>
    /// Throws a precise <see cref="DeploymentSiteServiceException"/> when the workload is
    /// missing any of the fields required for a Helm-based deploy: chart name,
    /// linked HelmRepositoryConfiguration, or repository URL. <c>ChartVersion</c>
    /// is intentionally NOT required — an empty value is the explicit "use the
    /// newest chart in the configured repository" opt-in (the operator's
    /// HelmRunner omits <c>--version</c> in that case, matching the dev/test
    /// rollout pattern seeded by the System.Communication.MainLatest blueprint).
    /// </summary>
    private async Task EnsureWorkloadIsHelmDeployableAsync(string tenantId, RtDeployableWorkload workload)
    {
        if (string.IsNullOrWhiteSpace(workload.ChartName))
        {
            throw DeploymentSiteServiceException.WorkloadMissingChartName(tenantId, workload.RtId, workload.Name);
        }

        var repo = await ResolveHelmRepositoryAsync(tenantId, workload);
        if (repo == null)
        {
            throw DeploymentSiteServiceException.WorkloadMissingHelmRepository(tenantId, workload.RtId, workload.Name);
        }
        if (string.IsNullOrWhiteSpace(repo.RepositoryUrl))
        {
            throw DeploymentSiteServiceException.WorkloadHelmRepositoryUrlEmpty(tenantId, workload.RtId, workload.Name);
        }

        // Ingress contract: when IngressEnabled is true we project ingress.enabled=true
        // + publicUri into the chart values. The chart's templates/ingress.yaml builds
        // host rules from publicUri, so an empty Hostname produces an Ingress with an
        // empty host — k8s admission rejects it and the helm release would fail
        // mid-rollout. Surface the misconfiguration as an actionable Deploy-time error
        // instead. ChartName / repo checks above mirror the same fail-fast pattern.
        if (workload.IngressEnabled && string.IsNullOrWhiteSpace(workload.Hostname))
        {
            throw DeploymentSiteServiceException.WorkloadIngressEnabledButHostnameEmpty(tenantId, workload.RtId, workload.Name);
        }

        // Validate template placeholders up-front so misconfigured workloads
        // fail with an actionable Deploy-time error instead of producing an
        // Ingress with the literal '{{...}}' as host (k8s admission rejects
        // mid-rollout) or a helm values file with unresolved placeholders.
        // Workloads that don't use template syntax pass through unchanged.
        var ctx = new WorkloadTemplateContext(tenantId);

        if (!string.IsNullOrWhiteSpace(workload.Hostname) &&
            !_templateResolver.TryResolve(workload.Hostname, ctx, out _, out var unknownInHostname))
        {
            throw DeploymentSiteServiceException.WorkloadTemplateUnknownPlaceholder(
                tenantId, workload.RtId, workload.Name, "Hostname", workload.Hostname, unknownInHostname!);
        }

        // Non-secret ValueOverrides flow through the resolver. Secret-flagged
        // entries are NOT validated/substituted here — the encryption layer
        // owns those values, and running templating over decrypted secret
        // material would mix two contracts.
        foreach (var v in workload.Values ?? Enumerable.Empty<RtValueOverrideRecord>())
        {
            if (v.IsSecret || string.IsNullOrEmpty(v.Value))
            {
                continue;
            }
            if (!_templateResolver.TryResolve(v.Value, ctx, out _, out var unknownInOverride))
            {
                throw DeploymentSiteServiceException.WorkloadTemplateUnknownPlaceholder(
                    tenantId, workload.RtId, workload.Name,
                    $"ValueOverride[{v.Path ?? string.Empty}]", v.Value, unknownInOverride!);
            }
        }

        if (!string.IsNullOrEmpty(workload.ValuesYaml) &&
            !_templateResolver.TryResolve(workload.ValuesYaml, ctx, out _, out var unknownInYaml))
        {
            throw DeploymentSiteServiceException.WorkloadTemplateUnknownPlaceholder(
                tenantId, workload.RtId, workload.Name, "ValuesYaml", workload.ValuesYaml, unknownInYaml!);
        }

        // AB#4984 lifecycle-mode validation. LifecycleMode is plain CK author configuration
        // (writable via GraphQL/blueprints without any service-layer hook), so the deploy is
        // the enforcement net: fail fast with an actionable error instead of deploying a
        // workload whose triggers would silently stop when the watchdog hibernates it.
        if (workload.LifecycleMode == RtLifecycleModeEnum.Auto)
        {
            throw DeploymentSiteServiceException.WorkloadLifecycleModeAutoNotImplemented(tenantId, workload.RtId, workload.Name);
        }

        if (workload.LifecycleMode == RtLifecycleModeEnum.OnDemand)
        {
            if (workload is not RtAdapter)
            {
                throw DeploymentSiteServiceException.WorkloadOnDemandNotSupportedForType(tenantId, workload.RtId, workload.Name);
            }

            var capability = await _onDemandCapabilityService.EvaluateAsync(tenantId,
                new RtEntityId(SystemCommunicationCkIds.RtCkAdapterTypeId, workload.RtId));
            if (!capability.IsCapable)
            {
                throw DeploymentSiteServiceException.WorkloadNotOnDemandCapable(tenantId, workload.RtId, workload.Name,
                    capability.BlockingReasons);
            }
        }

        // AB#4924 leasing validation. Same enforcement rationale as the AB#4984 block above:
        // LifecycleMode, SharingMode and the LentFrom* pair are plain CK author configuration with
        // no service-layer hook, so the deploy is the net. A leasing misconfiguration is
        // particularly worth failing loudly on, because its silent failure mode is a workload that
        // deploys successfully and then never executes anything.
        await EnsureLeasingConfigurationIsValidAsync(tenantId, workload);

        // AB#5027, deliberately NOT guarded here: the mandatory-service-account check lives on
        // the pipeline / data-flow deploy paths in AdapterService, not on the workload deploy.
        // Reasons: (a) this method also validates Applications, which execute no pipelines and
        // therefore have no pipeline identity; (b) the helm deploy of the adapter pod is the very
        // step that has to succeed before a service account can be provisioned onto it — gating
        // it on an already-linked account would be circular and would make every existing tenant's
        // adapter undeployable the moment this ships, ahead of the provisioning phase; (c) an
        // adapter with no pipelines is harmless. Enforcement stays tight regardless: no pipeline
        // can reach an adapter without a resolvable identity.
        //
        // Phase 2 does the opposite on this path: DeployWorkloadAsync PROVISIONS the adapter's
        // service account (best effort, after the deploy notification) rather than gating on it.
    }

    /// <inheritdoc />
    public async Task UndeployWorkloadAsync(string tenantId, OctoObjectId workloadRtId)
    {
        Logger.Info("[{TenantId}] Undeploying workload '{WorkloadRtId}'", tenantId, workloadRtId);

        var workload = await _communicationRepository.GetWorkloadByRtIdAsync(tenantId, workloadRtId);
        if (workload == null)
        {
            throw DeploymentSiteServiceException.WorkloadNotFound(tenantId, workloadRtId);
        }

        var deploymentSite = await _communicationRepository.GetDeploymentSiteForWorkloadAsync(tenantId, workload.RtId);
        if (deploymentSite == null)
        {
            throw DeploymentSiteServiceException.WorkloadNotInDeploymentSite(tenantId, workloadRtId);
        }

        // Reject when there's nothing to undeploy. Both Undeployed and
        // Disabled are terminal resting states — no helm release to remove.
        if (workload.DeploymentState == RtDeploymentStateEnum.Undeployed ||
            workload.DeploymentState == RtDeploymentStateEnum.Disabled)
        {
            throw DeploymentSiteServiceException.WorkloadAlreadyNotDeployed(tenantId, workloadRtId, workload.Name,
                workload.DeploymentState);
        }

        // Always go through the central-operator cleanup path even when
        // Environment is now Edge or Helm fields have since been cleared —
        // a helm release may still exist from a prior valid Deploy that
        // has not been cleaned up yet.
        await _operatorConnectionManager.NotifyWorkloadUndeployedAsync(new WorkloadUndeployedDto
        {
            TenantId = tenantId,
            DeploymentSiteRtId = deploymentSite.RtId.ToString(),
            WorkloadRtId = workload.RtId.ToString(),
            WorkloadName = workload.Name ?? string.Empty,
            WorkloadType = WorkloadWireMapping.ResolveWorkloadType(workload),
        });

        // Compute resting state. If the workload can no longer be deployed
        // (missing Helm fields), park it at Disabled; otherwise Undeployed so a
        // fresh deploy can be triggered. Edge deploymentSites are NOT a disabling rule —
        // an edge operator deploys workloads via the same helm path as central.
        var restingState = await IsWorkloadHelmDeployableAsync(tenantId, workload)
            ? RtDeploymentStateEnum.Undeployed
            : RtDeploymentStateEnum.Disabled;
        await SetWorkloadDeploymentStateAsync(tenantId, workload, restingState);

        // AB#4919: an undeployed workload has no lifecycle state to report. Left in the gauge it
        // would keep publishing its last value forever, showing a permanently hibernated workload
        // that no longer exists.
        WorkloadLifecycleMetrics.Forget(tenantId, workload.RtId);

        await _eventService.StoreInformationEventAsync(tenantId,
            $"Workload '{workload.Name}' undeploy requested (resting state: {restingState}).");

        await FanOutAdapterPoolMirrorsAsync(tenantId, workload);
    }

    /// <summary>
    ///     Pushes an adapter pool's change out to the mirrors its borrowers hold (AB#5271).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <c>DeploymentState</c> is one of the mirrored fields, and it is the one that explains a
    ///         queue which never drains — a borrower has no way to read it from the lender's database,
    ///         so a pool that goes down has to push. Deploy and undeploy are also the only pool events
    ///         this service owns: pools are RtEntities written through the asset repository, so a
    ///         rename or a scope change reaches the mirrors through the per-tenant reconcile on the
    ///         next tenant load (or a manual backfill) instead.
    ///     </para>
    ///     <para>
    ///         Best effort, exactly like the AB#5027 service-account provisioning on this path: a
    ///         mirror that could not be refreshed must not fail a deploy that has already been sent to
    ///         the operator. It is also cheap for the overwhelmingly common case — a pool that lends
    ///         to nobody resolves an empty borrower set and stops.
    ///     </para>
    /// </remarks>
    private async Task FanOutAdapterPoolMirrorsAsync(string tenantId, RtDeployableWorkload workload)
    {
        if (workload is not RtAdapterPool)
        {
            return;
        }

        try
        {
            await _adapterPoolMirrorProvisioningService.ProvisionForLenderAsync(tenantId);
        }
        catch (Exception e)
        {
            Logger.Error(e,
                "[{TenantId}] Could not refresh the lent adapter pool mirrors of adapter pool '{WorkloadName}' " +
                "({WorkloadRtId}); borrowers keep the mirror they have until the next reconcile",
                tenantId, workload.Name, workload.RtId);
        }
    }

    /// <inheritdoc />
    public async Task<int> ScaleAdapterPoolAsync(string tenantId, OctoObjectId poolWorkloadRtId, int replicas)
    {
        Logger.Info("[{TenantId}] Scaling adapter deploymentSite '{PoolWorkloadRtId}' to {Replicas} member(s)",
            tenantId, poolWorkloadRtId, replicas);

        var workload = await _communicationRepository.GetWorkloadByRtIdAsync(tenantId, poolWorkloadRtId);
        if (workload == null)
        {
            throw DeploymentSiteServiceException.WorkloadNotFound(tenantId, poolWorkloadRtId);
        }

        if (workload is not RtAdapterPool deploymentSite)
        {
            throw DeploymentSiteServiceException.WorkloadIsNotAnAdapterPool(tenantId, poolWorkloadRtId, workload.Name);
        }

        // Pending counts as scalable: a deploy that is still rolling out already has its
        // Deployments, and refusing here would make the first scale after a deploy a race.
        if (deploymentSite.DeploymentState != RtDeploymentStateEnum.Deployed &&
            deploymentSite.DeploymentState != RtDeploymentStateEnum.Pending)
        {
            throw DeploymentSiteServiceException.AdapterPoolNotDeployed(tenantId, poolWorkloadRtId, deploymentSite.Name,
                deploymentSite.DeploymentState);
        }

        // The clamp lives in RequestScaleAsync, not here: every caller of the scale verb has to be
        // held to the deploymentSite's range, and a guard that only covers the caller you thought of is the
        // guard that is missing during the incident.
        var effective = Math.Clamp(replicas, deploymentSite.MinReplicas, Math.Max(deploymentSite.MinReplicas, deploymentSite.MaxReplicas));
        await _workloadLifecycleService.RequestScaleAsync(tenantId, deploymentSite, replicas);

        await _eventService.StoreInformationEventAsync(tenantId,
            $"Adapter deploymentSite '{deploymentSite.Name}' scale to {effective} member(s) requested" +
            (effective == replicas
                ? "."
                : $" ({replicas} was outside the declared range {deploymentSite.MinReplicas}..{deploymentSite.MaxReplicas})."));

        return effective;
    }

    /// <summary>
    /// Resolves the deployment site name for a workload by walking the <c>Hosts</c>
    /// association back to its parent <c>RtDeploymentSite</c>. Returns null when the
    /// workload isn't currently in any deploymentSite.
    /// </summary>
    private async Task<string?> ResolvePoolNameForWorkloadAsync(string tenantId, RtDeployableWorkload workload)
    {
        var deploymentSite = await _communicationRepository.GetDeploymentSiteForWorkloadAsync(tenantId, workload.RtId);
        return deploymentSite?.Name;
    }

    /// <inheritdoc />
    public async Task UndeployDeploymentSiteAsync(string tenantId, OctoObjectId deploymentSiteRtId)
    {
        Logger.Info("[{TenantId}] Undeploying deploymentSite '{DeploymentSiteRtId}'", tenantId, deploymentSiteRtId);

        var rtDeploymentSite = await GetPoolByRtIdAsync(tenantId, deploymentSiteRtId);

        // Reject when there's nothing to undeploy. Both Undeployed and
        // Disabled are terminal resting states — the operator has no CR /
        // broker secret to remove.
        if (rtDeploymentSite.DeploymentState == RtDeploymentStateEnum.Undeployed ||
            rtDeploymentSite.DeploymentState == RtDeploymentStateEnum.Disabled)
        {
            throw DeploymentSiteServiceException.PoolAlreadyNotDeployed(tenantId, deploymentSiteRtId, rtDeploymentSite.Name,
                rtDeploymentSite.DeploymentState);
        }

        var deploymentSiteName = rtDeploymentSite.Name ?? string.Empty;

        // Helm uninstall managed workloads before tearing down the deploymentSite
        // itself — the operator removes the DeploymentSite CR last so
        // it can still resolve the deploymentSite's namespace while uninstalling.
        // We always go through the central-operator cleanup path even when
        // Environment is now Edge: the user may have switched a Cloud deploymentSite
        // to Edge without first undeploying, and the CR/secret still exists
        // in the central cluster and must be removed.
        await UndeployManagedWorkloadsAsync(tenantId, deploymentSiteRtId, deploymentSiteName);

        Logger.Info(
            "[{TenantId}] DeploymentSite '{DeploymentSiteName}' (rtId {DeploymentSiteRtId}): notifying central Communication Operator to clean up (Environment={Environment})",
            tenantId, deploymentSiteName, deploymentSiteRtId, rtDeploymentSite.Environment);
        await _operatorConnectionManager.NotifyDeploymentSiteUndeployedAsync(tenantId, deploymentSiteRtId.ToString());

        // Resting state after undeploy: Disabled when the deploymentSite can no longer
        // be deployed via this controller (Edge), else Undeployed.
        var restingState = rtDeploymentSite.Environment == RtEnvironmentEnum.Edge
            ? RtDeploymentStateEnum.Disabled
            : RtDeploymentStateEnum.Undeployed;
        await _communicationRepository.SetDeploymentSiteDeploymentStateAsync(tenantId, deploymentSiteRtId, restingState);

        await _eventService.StoreInformationEventAsync(tenantId,
            $"DeploymentSite '{deploymentSiteName}' undeployed (resting state: {restingState}).");
    }

    private async Task DeployManagedWorkloadsAsync(string tenantId, OctoObjectId deploymentSiteRtId, string deploymentSiteName)
    {
        IReadOnlyCollection<RtDeployableWorkload> workloads;
        try
        {
            workloads = await _communicationRepository.GetWorkloadsForDeploymentSiteAsync(tenantId, deploymentSiteRtId);
        }
        catch (Exception ex)
        {
            Logger.Warn(ex,
                "[{TenantId}] Failed to enumerate managed workloads of deploymentSite '{DeploymentSiteName}'; deploymentSite is deployed but no workloads were fanned out",
                tenantId, deploymentSiteName);
            return;
        }

        if (workloads.Count == 0)
        {
            Logger.Info("[{TenantId}] DeploymentSite '{DeploymentSiteName}' has no managed workloads", tenantId, deploymentSiteName);
            return;
        }

        Logger.Info("[{TenantId}] DeploymentSite '{DeploymentSiteName}' has {Count} managed workload(s) to deploy",
            tenantId, deploymentSiteName, workloads.Count);

        foreach (var workload in workloads)
        {
            try
            {
                var dto = await BuildWorkloadDeployedDtoAsync(tenantId, deploymentSiteRtId, deploymentSiteName, workload);
                if (dto == null)
                {
                    Logger.Warn(
                        "[{TenantId}] Workload '{WorkloadName}' is incomplete — skipping deploy",
                        tenantId, workload.Name ?? string.Empty);
                    continue;
                }

                await _operatorConnectionManager.NotifyWorkloadDeployedAsync(dto);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex,
                    "[{TenantId}] Failed to deploy workload '{WorkloadName}' of deploymentSite '{DeploymentSiteName}'",
                    tenantId, workload.Name ?? string.Empty, deploymentSiteName);
            }
        }
    }

    private async Task UndeployManagedWorkloadsAsync(string tenantId, OctoObjectId deploymentSiteRtId, string deploymentSiteName)
    {
        // Read from in-memory tracking only — same rationale as
        // UndeployAllCloudDeploymentSitesAsync, this path may run during tenant delete
        // where the repository is already torn down.
        var poolRtIdString = deploymentSiteRtId.ToString();
        var tracked = _operatorConnectionManager.GetDeployedWorkloadsForTenant(tenantId)
            .Where(w => w.DeploymentSiteRtId == poolRtIdString)
            .ToArray();

        if (tracked.Length == 0)
        {
            return;
        }

        Logger.Info("[{TenantId}] Undeploying {Count} workload(s) of deploymentSite '{DeploymentSiteName}'",
            tenantId, tracked.Length, deploymentSiteName);

        foreach (var workload in tracked)
        {
            try
            {
                await _operatorConnectionManager.NotifyWorkloadUndeployedAsync(workload);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex,
                    "[{TenantId}] Failed to undeploy workload '{WorkloadName}' of deploymentSite '{DeploymentSiteName}'",
                    tenantId, workload.WorkloadName, deploymentSiteName);
            }
        }
    }

    private async Task<WorkloadDeployedDto?> BuildWorkloadDeployedDtoAsync(string tenantId,
        OctoObjectId deploymentSiteRtId, string deploymentSiteName, RtDeployableWorkload workload,
        bool isReconciliation = false)
    {
        // ChartName is the minimal Helm identity we need to talk to a repository;
        // ChartVersion is optional and means "latest" when empty (see
        // EnsureWorkloadIsHelmDeployableAsync for the contract).
        if (string.IsNullOrWhiteSpace(workload.ChartName))
        {
            return null;
        }

        var repo = await ResolveHelmRepositoryAsync(tenantId, workload);
        if (repo == null || string.IsNullOrWhiteSpace(repo.RepositoryUrl))
        {
            return null;
        }

        var ctx = new WorkloadTemplateContext(tenantId);
        var overrides = (workload.Values ?? Enumerable.Empty<RtValueOverrideRecord>())
            .Select(v => new ValueOverrideDto
            {
                Path = v.Path ?? string.Empty,
                // Secret values flow through Decrypt only; template substitution
                // is deliberately skipped so encryption-sentinel and template
                // layers stay decoupled (see EnsureWorkloadIsHelmDeployableAsync).
                // Non-secret values are substituted; EnsureWorkloadIsHelmDeployableAsync
                // has already validated every placeholder, so TryResolve cannot
                // fail here.
                Value = v.IsSecret
                    ? _encryptionService.Decrypt(v.Value ?? string.Empty)
                    : ResolveTemplate(v.Value, ctx) ?? string.Empty,
                IsSecret = v.IsSecret,
            })
            .ToArray();

        overrides = await AppendPipelineServiceAccountOverridesAsync(tenantId, workload, overrides);
        overrides = AppendAdapterPoolMemberOverrides(tenantId, workload, overrides);

        return new WorkloadDeployedDto
        {
            TenantId = tenantId,
            DeploymentSiteRtId = deploymentSiteRtId.ToString(),
            WorkloadName = workload.Name ?? string.Empty,
            WorkloadRtId = workload.RtId.ToString(),
            WorkloadType = WorkloadWireMapping.ResolveWorkloadType(workload),
            RepositoryUrl = repo.RepositoryUrl,
            RepositoryUsername = repo.Username,
            RepositoryPassword = string.IsNullOrEmpty(repo.Password)
                ? null
                : _encryptionService.Decrypt(repo.Password),
            ChartName = workload.ChartName,
            // Coalesce a null/missing ChartVersion to empty string — the DTO is
            // non-nullable on the operator side and an empty value carries the
            // "use latest from configured repo" contract (the operator's
            // HelmRunner omits --version when blank).
            ChartVersion = workload.ChartVersion ?? string.Empty,
            // AB#4955: tells the operator this dispatch restores what was already running rather
            // than acting on a release decision, so an unpinned workload stays on the chart version
            // it currently has installed instead of resolving the newest one again.
            IsReconciliation = isReconciliation,
            // Same template resolution as for non-secret ValueOverrides — already
            // validated by EnsureWorkloadIsHelmDeployableAsync.
            ValuesYaml = ResolveTemplate(workload.ValuesYaml, ctx) ?? string.Empty,
            Values = overrides,
            // Lives on DeployableWorkload so both Adapter and Application can
            // opt in. Applications with a backend (e.g. energy-community,
            // voest-app) need cluster credentials just like in-cluster adapters.
            // 🔴 AB#4924: never for an adapter deploymentSite, whatever the entity says. The flag hands the
            // workload the cluster's SHARED Mongo / CrateDB credentials, and a deploymentSite member runs
            // work for tenants other than the one that owns it — a standing credential to every
            // tenant's data would make the lease that grants it one tenant at a time meaningless.
            // The operator refuses the same thing independently; either gate alone is one edit
            // away from silence.
            ReceivesClusterSecrets = workload is not RtAdapterPool && workload.ReceivesClusterSecrets,
            // Public-ingress opt-in. The operator projects ingress.enabled=true
            // and publicUri into the workload's Helm values when this is set —
            // cluster-wide ingress defaults (className, cluster-issuer, TLS)
            // come from operator config. Hostname is normalised to null when
            // blank so the DTO matches the operator-side "set or absent"
            // contract on the chart values. Any {{domain.NAME}} placeholder is
            // resolved at this point against the controller's configured named
            // domains; EnsureWorkloadIsHelmDeployableAsync has already validated
            // that every referenced NAME exists, so TryResolve cannot fail here.
            IngressEnabled = workload.IngressEnabled,
            Hostname = ResolveHostname(workload.Hostname, ctx),
            // AB#4917: a redeploy of a hibernated/draining workload must not
            // resurrect it — the operator pins the release at replicaCount=0.
            // A deploy that is supposed to wake the workload goes through the
            // wake gate first, which moves the state to Waking/Running before
            // the deploy event is built.
            Hibernated = workload.LifecycleState is RtLifecycleStateEnum.Hibernated
                or RtLifecycleStateEnum.Draining,
        };
    }

    /// <summary>
    /// Projects the adapter's provisioned <c>ServiceAccountConfiguration</c> (AB#5027) onto the two
    /// Helm value paths the adapter chart reads its own OAuth credentials from (AB#5072), so the pod
    /// can authenticate its <c>/{tenantId}/adapterHub</c> connection instead of coming up anonymous.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Adapters only.</b> Only Adapters execute pipelines and only Adapters connect to the adapter
    /// hub; <c>Application</c>s have no pipeline identity and the association does not even exist on
    /// their type, so they are skipped before any repository read.
    /// </para>
    /// <para>
    /// <b>Deliberately NOT gated on <c>ReceivesClusterSecrets</c>.</b> That flag decides whether a
    /// workload receives the *cluster's* shared data-store credentials (MongoDB, CrateDB) — a pure
    /// edge adapter must not get them. These two values are the opposite kind of thing: the adapter's
    /// own, per-adapter, tenant-scoped identity, and an edge adapter needs it *more* than an
    /// in-cluster one, because it is the only credential it presents when it dials into the
    /// controller across the network. The precedent is the RabbitMQ broker password, which is
    /// injected unconditionally for exactly the same reason — every workload needs the command bus,
    /// and every adapter needs its identity.
    /// </para>
    /// <para>
    /// 🔴 <b>The secret never reaches a log path.</b> It is read, decrypted and put on the DTO;
    /// it is not logged at any level, not truncated, and not carried in an exception message. The
    /// warning paths below deliberately name only the adapter and the missing attribute.
    /// </para>
    /// </remarks>
    private async Task<ValueOverrideDto[]> AppendPipelineServiceAccountOverridesAsync(string tenantId,
        RtDeployableWorkload workload, ValueOverrideDto[] overrides)
    {
        if (workload is not RtAdapter adapter)
        {
            return overrides;
        }

        RtServiceAccountConfiguration? serviceAccount;
        try
        {
            serviceAccount = await _serviceAccountResolver.GetAdapterDefaultAsync(tenantId, adapter.RtId);
        }
        catch (Exception e)
        {
            // Same rule as everywhere else on this path: a workload whose credentials cannot be read
            // still deploys, it just comes up anonymous — which is what the whole fleet does today.
            // Failing the deploy here would make an identity/CK-cache hiccup undeployable.
            Logger.Warn(e,
                "[{TenantId}] Could not read the pipeline service account of adapter '{WorkloadName}' ({WorkloadRtId}); it is deployed without its own credentials and connects to the adapter hub anonymously",
                tenantId, workload.Name, workload.RtId);
            return overrides;
        }

        if (serviceAccount == null)
        {
            // Provisioning runs immediately before this on the deploy path, so reaching here means
            // it failed (and wrote its own error event) or nothing is linked yet on a path that does
            // not provision. Debug, not warning: the failing side already reported it.
            Logger.Debug(
                "[{TenantId}] Adapter '{WorkloadName}' ({WorkloadRtId}) has no linked pipeline service account; deploying without adapter credentials",
                tenantId, workload.Name, workload.RtId);
            return overrides;
        }

        // Read through GetAttributeValueOrDefault, never the generated properties: all four
        // attributes are mandatory on the CK type, so a generated getter throws
        // InvalidAttributeValueException on a half-written entity — which must degrade to "no
        // credentials", not to a failed deploy. Same reasoning as
        // PipelineServiceAccountProvisioningService.ReadAttribute.
        var clientId =
            serviceAccount.GetAttributeValueOrDefault(nameof(RtServiceAccountConfiguration.ClientId)) as string;
        var clientSecret =
            serviceAccount.GetAttributeValueOrDefault(nameof(RtServiceAccountConfiguration.ClientSecret)) as string;

        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
        {
            Logger.Warn(
                "[{TenantId}] The pipeline service account of adapter '{WorkloadName}' ({WorkloadRtId}) is missing its client id or secret; deploying without adapter credentials",
                tenantId, workload.Name, workload.RtId);
            return overrides;
        }

        // An operator who pinned either path on the workload entity itself wins: the override list
        // is last-wins in WorkloadOverrideYamlBuilder, so appending unconditionally would silently
        // overrule a deliberate manual pin.
        var result = new List<ValueOverrideDto>(overrides);
        AddUnlessPresent(result, ServiceAccountClientIdValuePath, clientId!, false);
        AddUnlessPresent(result, ServiceAccountClientSecretValuePath,
            // The provisioning path writes this attribute in plaintext, so this is normally a
            // pass-through. It goes through Decrypt anyway to sit in the same lane as every other
            // secret leaving this method — if the attribute ever carries the `enc:v1:` sentinel, the
            // wire must still carry the plaintext (Decrypt is a no-op without the sentinel).
            _encryptionService.Decrypt(clientSecret!), true);
        return result.ToArray();

        static void AddUnlessPresent(List<ValueOverrideDto> target, string path, string value, bool isSecret)
        {
            if (target.Any(v => string.Equals(v.Path, path, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }
            target.Add(new ValueOverrideDto { Path = path, Value = value, IsSecret = isSecret });
        }
    }

    /// <summary>
    ///     Projects an <see cref="RtAdapterPool" />'s replica range and per-member sizing onto the
    ///     chart values every member is rendered from (AB#4924 §7.2, concept §8 Q15).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A deploymentSite is <b>one workload with a replica range</b>, not N entities — so there is no new
    ///         deployment concept here, only <c>replicaCount</c> plus the four <c>resources.*</c>
    ///         paths on the same Helm release the operator already installs 1:1. Q15's "one sizing per
    ///         deploymentSite" is literally what a chart value is: it renders identically into every replica.
    ///     </para>
    ///     <para>
    ///         The release starts at <c>MinReplicas</c>. Everything above that is a scaling decision
    ///         driven by queue pressure and carried by the AB#4917 <c>ScaleWorkloadDto</c> verb, which
    ///         patches replicas without touching the release — so a scaled-up deploymentSite is not reverted by
    ///         the next unrelated reconcile the way a value-file replica count would be.
    ///     </para>
    ///     <para>
    ///         An override the author pinned on the entity wins, same last-wins rule as the service
    ///         account credentials: a value that silently overrules a deliberate pin is worse than a
    ///         value that is absent.
    ///     </para>
    /// </remarks>
    private static ValueOverrideDto[] AppendAdapterPoolMemberOverrides(string tenantId,
        RtDeployableWorkload workload, ValueOverrideDto[] overrides)
    {
        if (workload is not RtAdapterPool deploymentSite)
        {
            return overrides;
        }

        var result = new List<ValueOverrideDto>(overrides);

        // 🔴 AB#4924 §9.4 — without these two the pod is not a deploymentSite member at all. It starts, binds
        // AdapterPoolMemberOptions to its defaults, finds IsEnabled false, logs "started without a
        // configured deploymentSite … Doing nothing" and sits there looking healthy: the SDK composes a member
        // only when BOTH ids are present. Sizing and replica count alone, which is all this method
        // used to write, produce exactly that pod.
        //
        // The tenant is the LENDER — the tenant that owns the deploymentSite entity, i.e. the one this
        // workload is being deployed for. It is the member's CONNECTION tenant, never the tenant of
        // any work it executes; that arrives per lease. See AdapterPoolMemberOptions.
        //
        // 🔴 No member id. AdapterPoolMemberOptions.EffectiveMemberId falls back to
        // Environment.MachineName, which in Kubernetes IS the pod name — the value an operator would
        // search for anyway, and the only one that stays correct when a replica is rescheduled.
        // Writing a value here would pin every replica of the deployment to the same member id, and
        // the controller's registry keys members by it.
        //
        // 🔴 The two paths are `adapterPool.poolTenantId` and `adapterPool.poolRtId`, and they are a
        // CHART CONTRACT — octo-mesh-adapter's `octo-mesh.isPoolMember` is literally
        // `and .Values.adapterPool.poolTenantId .Values.adapterPool.poolRtId`. The DeploymentSite
        // rename renamed the first one here and not in the chart, which is invisible until a pool
        // is actually deployed: `isPoolMember` then reads false, the chart renders the member as an
        // ordinary adapter, and helm fails on `secrets.databaseUser must be set` — a credential the
        // pool is designed never to receive. The tell was that only ONE of the two was renamed.
        AddUnlessPinned(result, "adapterPool.poolTenantId", tenantId);
        AddUnlessPinned(result, "adapterPool.poolRtId", deploymentSite.RtId.ToString());

        AddUnlessPinned(result, "replicaCount", deploymentSite.MinReplicas.ToString(CultureInfo.InvariantCulture));
        AddUnlessPinned(result, "resources.requests.cpu", deploymentSite.PoolMemberCpuRequest);
        AddUnlessPinned(result, "resources.limits.cpu", deploymentSite.PoolMemberCpuLimit);
        AddUnlessPinned(result, "resources.requests.memory", deploymentSite.PoolMemberMemoryRequest);
        AddUnlessPinned(result, "resources.limits.memory", deploymentSite.PoolMemberMemoryLimit);

        return result.ToArray();

        static void AddUnlessPinned(List<ValueOverrideDto> target, string path, string? value)
        {
            // An unset sizing attribute means "whatever the chart defaults to", not "empty string":
            // rendering `resources.requests.cpu: ""` into the values file makes the pod spec invalid
            // and the release fails on admission.
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            if (target.Any(v => string.Equals(v.Path, path, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            target.Add(new ValueOverrideDto { Path = path, Value = value, IsSecret = false });
        }
    }

    private string? ResolveHostname(string? hostname, WorkloadTemplateContext ctx)
    {
        if (string.IsNullOrWhiteSpace(hostname))
        {
            return null;
        }
        _templateResolver.TryResolve(hostname, ctx, out var resolved, out _);
        return string.IsNullOrWhiteSpace(resolved) ? null : resolved;
    }

    private string? ResolveTemplate(string? template, WorkloadTemplateContext ctx)
    {
        if (string.IsNullOrEmpty(template))
        {
            return template;
        }
        _templateResolver.TryResolve(template, ctx, out var resolved, out _);
        return resolved;
    }

    /// <inheritdoc />
    public async Task UndeployAllCloudDeploymentSitesAsync(string tenantId)
    {
        Logger.Info("[{TenantId}] Undeploying all Cloud deploymentSites (tenant cleanup)", tenantId);

        // Read from the operator connection manager's in-memory tracking
        // rather than the tenant repository. PreDeleteTenant fires in parallel
        // with PreUpdatePreDeleteTenantConsumer (octo-common-services), which
        // unloads the CK-cache for the tenant. If we hit the repository here
        // we race and get "Failed to get deploymentSites" — and the operator is never
        // told to clean up, leaving the DeploymentSite CR and broker
        // secret orphaned in the cluster.
        var deployedDeploymentSites = _operatorConnectionManager.GetDeployedDeploymentSitesForTenant(tenantId);
        var trackedWorkloads = _operatorConnectionManager.GetDeployedWorkloadsForTenant(tenantId);

        if (deployedDeploymentSites.Count == 0 && trackedWorkloads.Count == 0)
        {
            Logger.Info("[{TenantId}] No Cloud deploymentSites or workloads to clean up", tenantId);
            return;
        }

        // Tear down workloads first so the operator can helm uninstall while
        // the deploymentSite namespace is still around.
        foreach (var workload in trackedWorkloads)
        {
            try
            {
                await _operatorConnectionManager.NotifyWorkloadUndeployedAsync(workload);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex,
                    "[{TenantId}] Failed to notify operator of workload undeploy during tenant cleanup, workload '{WorkloadName}' (rtId {WorkloadRtId}, deployment site rtId {DeploymentSiteRtId})",
                    tenantId, workload.WorkloadName, workload.WorkloadRtId, workload.DeploymentSiteRtId);
            }
        }

        foreach (var deploymentSiteRtId in deployedDeploymentSites)
        {
            try
            {
                await _operatorConnectionManager.NotifyDeploymentSiteUndeployedAsync(tenantId, deploymentSiteRtId);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex,
                    "[{TenantId}] Failed to notify operator of deploymentSite undeploy during tenant cleanup, deployment site rtId {DeploymentSiteRtId}",
                    tenantId, deploymentSiteRtId);
            }
        }

        await _eventService.StoreInformationEventAsync(tenantId,
            $"Notified central Communication Operator to undeploy {trackedWorkloads.Count} workload(s) and {deployedDeploymentSites.Count} Cloud deploymentSite(s) for tenant cleanup.");
    }

    private async Task<RtDeploymentSite> GetPoolByRtIdAsync(string tenantId, OctoObjectId deploymentSiteRtId)
    {
        var deploymentSites = await _communicationRepository.GetDeploymentSitesAsync(tenantId);
        var rtDeploymentSite = deploymentSites.FirstOrDefault(p => p.RtId == deploymentSiteRtId);
        if (rtDeploymentSite == null)
        {
            throw DeploymentSiteServiceException.DeploymentSiteNotFound(tenantId, deploymentSiteRtId);
        }
        return rtDeploymentSite;
    }

    /// <inheritdoc />
    public async Task SetCommunicationStateOfflineAsync(string tenantId, OctoObjectId deploymentSiteRtId)
    {
        Logger.Info("[{TenantId}] Setting deploymentSite '{DeploymentSiteRtId}' offline", tenantId, deploymentSiteRtId);

        if (!_deploymentSiteCache.TryGetTenant(tenantId, out var deploymentSiteTenant))
        {
            throw DeploymentSiteServiceException.TenantNotFoundOrNotEnabled(tenantId);
        }

        if (deploymentSiteTenant.DeploymentSitesById.TryGetValue(deploymentSiteRtId, out var deploymentSiteDescription))
        {
            await _communicationRepository.SetDeploymentSiteCommunicationStateAsync(tenantId, deploymentSiteDescription.DeploymentSiteRtId,
                RtCommunicationStateEnum.Offline);
        }
    }

    /// <inheritdoc />
    public async Task SetCommunicationStateOfflineAsync(string tenantId, OctoObjectId deploymentSiteRtId,
        string disconnectingConnectionId)
    {
        if (!_deploymentSiteCache.TryGetTenant(tenantId, out var deploymentSiteTenant))
        {
            return;
        }

        if (!deploymentSiteTenant.DeploymentSitesById.TryGetValue(deploymentSiteRtId, out var deploymentSiteDescription))
        {
            return;
        }

        // Multi-claim guard: more than one operator connection can claim the
        // same deploymentSite at the same time — central operator with replicas, or a
        // brief rolling-upgrade overlap where the new pod has registered but
        // the old pod's SignalR connection has not yet timed out. The
        // DeploymentSiteDescription cache only remembers the LAST claim's ConnectionId,
        // so the disconnect of one claimer would silently flip the deploymentSite
        // Offline even though another connection is still hosting it
        // (caller passed RemoveOperator's orphan list, which only filters
        // claims made by the disconnecting connection, not all live claims).
        //
        // OperatorConnectionManager.RemoveOperator has already cleared the
        // disconnecting connection's tracking entry by the time we get here,
        // so any results from GetConnectionsForDeploymentSite are surviving operators.
        var stillClaiming = _operatorConnectionManager.GetConnectionsForDeploymentSite(tenantId, deploymentSiteRtId.ToString());
        if (stillClaiming.Count > 0)
        {
            // Keep the deploymentSite Online and rewire the cache to a surviving
            // connection so the stale-disconnect guard below works correctly
            // when THAT one eventually disconnects too.
            deploymentSiteDescription.UpdateConnectionId(tenantId, stillClaiming[0]);
            Logger.Info(
                "[{TenantId}] deploymentSite '{DeploymentSiteRtId}' stays online after disconnect of " +
                "'{OldConnectionId}': {Count} other operator connection(s) still claim it; " +
                "cache rewired to '{NewConnectionId}'",
                tenantId, deploymentSiteRtId, disconnectingConnectionId, stillClaiming.Count,
                stillClaiming[0]);
            return;
        }

        // Stale-disconnect guard: if a newer connection has already taken over this
        // deploymentSite (e.g. the operator reconnected after a controller restart and the old
        // connection's OnDisconnectedAsync is only now firing), we must not flip
        // Online → Offline. Mirrors the adapter pattern in
        // AdapterService.SetAdapterCommunicationStateOfflineAsync.
        if (!string.IsNullOrWhiteSpace(deploymentSiteDescription.ConnectionId) &&
            deploymentSiteDescription.ConnectionId != disconnectingConnectionId)
        {
            Logger.Warn(
                "[{TenantId}] ignoring stale disconnect for deploymentSite '{DeploymentSiteRtId}': cached connection " +
                "'{CurrentConnectionId}' has replaced disconnecting connection '{OldConnectionId}'",
                tenantId, deploymentSiteRtId, deploymentSiteDescription.ConnectionId, disconnectingConnectionId);
            return;
        }

        deploymentSiteDescription.RemoveConnectionId(tenantId);
        await SetCommunicationStateOfflineAsync(tenantId, deploymentSiteDescription.DeploymentSiteRtId);
    }

    /// <inheritdoc />
    public async Task SetCommunicationStateOnlineAsync(string tenantId, OctoObjectId deploymentSiteRtId)
    {
        Logger.Info("[{TenantId}] Setting deploymentSite '{DeploymentSiteRtId}' online", tenantId, deploymentSiteRtId);

        if (!_deploymentSiteCache.TryGetTenant(tenantId, out var deploymentSiteTenant))
        {
            throw DeploymentSiteServiceException.TenantNotFoundOrNotEnabled(tenantId);
        }

        if (deploymentSiteTenant.DeploymentSitesById.TryGetValue(deploymentSiteRtId, out var deploymentSiteDescription))
        {
            await _communicationRepository.SetDeploymentSiteCommunicationStateAsync(tenantId, deploymentSiteDescription.DeploymentSiteRtId,
                RtCommunicationStateEnum.Online);
        }
    }

    /// <inheritdoc />
    public async Task SetCommunicationStateOnlineAsync(string tenantId, OctoObjectId deploymentSiteRtId, string connectionId)
    {
        Logger.Info("[{TenantId}] Setting deploymentSite '{DeploymentSiteRtId}' online (connection '{ConnectionId}')",
            tenantId, deploymentSiteRtId, connectionId);

        if (!_deploymentSiteCache.TryGetTenant(tenantId, out var deploymentSiteTenant))
        {
            throw DeploymentSiteServiceException.TenantNotFoundOrNotEnabled(tenantId);
        }

        // Lazy-load the deploymentSite into the cache on first sight. The legacy /poolHub
        // path relied on RegisterPoolOperatorAsync (which also touched the
        // deploymentSite's DeploymentState) to populate the cache; the new /operatorHub
        // RegisterDeploymentSiteAsync is purely about CommunicationState, so we just
        // ensure the cache is populated here without touching DeploymentState.
        if (!deploymentSiteTenant.DeploymentSitesById.TryGetValue(deploymentSiteRtId, out var deploymentSiteDescription))
        {
            var deploymentSites = await _communicationRepository.GetDeploymentSitesAsync(tenantId);
            var rtDeploymentSite = deploymentSites.FirstOrDefault(p => p.RtId == deploymentSiteRtId);
            if (rtDeploymentSite == null)
            {
                Logger.Warn("[{TenantId}] Cannot set deploymentSite '{DeploymentSiteRtId}' online — not found in repository",
                    tenantId, deploymentSiteRtId);
                return;
            }
            deploymentSiteDescription = deploymentSiteTenant.AddDeploymentSite(rtDeploymentSite.Name ?? string.Empty, rtDeploymentSite.RtId, connectionId);
        }
        else
        {
            deploymentSiteDescription.UpdateConnectionId(tenantId, connectionId);
        }

        await SetCommunicationStateOnlineAsync(tenantId, deploymentSiteDescription.DeploymentSiteRtId);
    }

    public async Task<IReadOnlyList<DeploymentSiteSummaryDto>> GetDeploymentSiteSummariesAsync(string tenantId)
    {
        var deploymentSites = await _communicationRepository.GetDeploymentSitesAsync(tenantId);
        return deploymentSites.Select(p => new DeploymentSiteSummaryDto
        {
            RtId = p.RtId.ToString(),
            Name = p.Name ?? string.Empty,
            Description = p.Description,
            CommunicationState = (CommunicationState)(int)p.CommunicationState,
            ConfigurationState = (ConfigurationState)(int)p.ConfigurationState,
            DeploymentState = (EntityDeploymentState)(int)p.DeploymentState,
            CommunicationStateTimestamp = p.CommunicationStateTimestamp,
            StatusMessage = p.StatusMessage
        }).ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ActiveDeployment>> GetActiveDeploymentsAsync(string tenantId)
    {
        var deploymentSites = await _communicationRepository.GetDeploymentSitesAsync(tenantId);
        var workloads = await _communicationRepository.GetWorkloadsAsync(tenantId);

        var active = new List<ActiveDeployment>();
        active.AddRange(deploymentSites
            .Where(p => ActiveDeployment.IsActive(p.DeploymentState))
            .Select(p => new ActiveDeployment(ActiveDeployment.DeploymentSiteKind, DisplayName(p.Name, p.RtId), p.DeploymentState))
            .OrderBy(d => d.Name, StringComparer.Ordinal));
        active.AddRange(workloads
            .Where(w => ActiveDeployment.IsActive(w.DeploymentState))
            .Select(w => new ActiveDeployment(
                w is RtApplication ? ActiveDeployment.ApplicationKind : ActiveDeployment.AdapterKind,
                DisplayName(w.Name, w.RtId), w.DeploymentState))
            .OrderBy(d => d.Name, StringComparer.Ordinal));

        return active;
    }

    private static string DisplayName(string? name, OctoObjectId rtId)
    {
        return string.IsNullOrWhiteSpace(name) ? rtId.ToString() : name;
    }

    /// <inheritdoc />
    public async Task RecomputeAllDeploymentStatesAsync(string tenantId)
    {
        Logger.Info("[{TenantId}] Recomputing all deployment states", tenantId);

        var poolsUpdated = 0;
        var workloadsUpdated = 0;
        var pipelinesUpdated = 0;
        var triggersUpdated = 0;

        // 1) DeploymentSites: Edge → Disabled, Cloud → leave (controller-managed lifecycle)
        IReadOnlyCollection<RtDeploymentSite> deploymentSites;
        try
        {
            deploymentSites = await _communicationRepository.GetDeploymentSitesAsync(tenantId);
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "[{TenantId}] Failed to enumerate deploymentSites during deployment-state recompute", tenantId);
            return;
        }

        // Track adapter Disabled state so pipelines can inherit it without an extra DB hit per pipeline.
        var disabledAdapterRtIds = new HashSet<OctoObjectId>();

        foreach (var deploymentSite in deploymentSites)
        {
            try
            {
                // Only flip resting states. A deploymentSite currently Deployed/Pending/Error
                // owns real operator resources (DeploymentSite CR, broker secret)
                // and must stay until an explicit Undeploy. The user who switches a
                // Cloud deploymentSite to Edge while it is Deployed sees Deployed correctly
                // and can clean up via the Undeploy command.
                if (deploymentSite.DeploymentState == RtDeploymentStateEnum.Undeployed ||
                    deploymentSite.DeploymentState == RtDeploymentStateEnum.Disabled)
                {
                    var poolTarget = deploymentSite.Environment == RtEnvironmentEnum.Edge
                        ? RtDeploymentStateEnum.Disabled
                        : RtDeploymentStateEnum.Undeployed;
                    if (poolTarget != deploymentSite.DeploymentState)
                    {
                        await _communicationRepository.SetDeploymentSiteDeploymentStateAsync(tenantId, deploymentSite.RtId, poolTarget);
                        poolsUpdated++;
                    }
                }

                // 2) Workloads in this deploymentSite
                var workloads = await _communicationRepository.GetWorkloadsForDeploymentSiteAsync(tenantId, deploymentSite.RtId);
                foreach (var workload in workloads)
                {
                    var target = await ComputeWorkloadTargetStateAsync(tenantId, workload, deploymentSite);
                    if (target.HasValue && target.Value != workload.DeploymentState)
                    {
                        await SetWorkloadDeploymentStateAsync(tenantId, workload, target.Value);
                        workloadsUpdated++;
                    }

                    // Track adapters that ended up (or stayed) Disabled so pipelines
                    // can inherit. A Deployed adapter — even one in an Edge deploymentSite
                    // post-env-switch — is still physically running, so its pipelines
                    // are not inherited-Disabled.
                    var endStateIsDisabled = (target ?? workload.DeploymentState) == RtDeploymentStateEnum.Disabled;
                    if (workload is RtAdapter && endStateIsDisabled)
                    {
                        disabledAdapterRtIds.Add(workload.RtId);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(ex,
                    "[{TenantId}] Failed to recompute deployment state for deploymentSite '{DeploymentSiteName}' or its workloads",
                    tenantId, deploymentSite.Name ?? deploymentSite.RtId.ToString());
            }
        }

        // 3) Pipelines: Disabled if no adapter or adapter is Disabled
        IReadOnlyCollection<RtPipeline> pipelines;
        try
        {
            pipelines = await _communicationRepository.GetAllPipelinesAsync(tenantId);
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "[{TenantId}] Failed to enumerate pipelines during deployment-state recompute", tenantId);
            pipelines = Array.Empty<RtPipeline>();
        }

        var disabledPipelineRtIds = new HashSet<OctoObjectId>();
        foreach (var pipeline in pipelines)
        {
            try
            {
                var pipelineRtEntityId = new RtEntityId(SystemCommunicationCkIds.RtCkPipelineTypeId, pipeline.RtId);
                var adapter = await _communicationRepository.GetAdapterByPipelineAsync(tenantId, pipelineRtEntityId);

                var ruleSaysDisabled = adapter == null || disabledAdapterRtIds.Contains(adapter.RtId);

                // Only touch resting states — a Deployed pipeline is physically pushed
                // to a running adapter and must be Undeployed via the proper command,
                // not silently flipped.
                if (pipeline.DeploymentState == RtDeploymentStateEnum.Undeployed ||
                    pipeline.DeploymentState == RtDeploymentStateEnum.Disabled)
                {
                    var target = ruleSaysDisabled
                        ? RtDeploymentStateEnum.Disabled
                        : RtDeploymentStateEnum.Undeployed;
                    if (target != pipeline.DeploymentState)
                    {
                        await _communicationRepository.SetPipelineDeploymentStateAsync(tenantId,
                            pipelineRtEntityId, target, null);
                        pipelinesUpdated++;
                    }
                }

                // Pipeline is treated as "currently effectively disabled" for trigger
                // inheritance only when it actually ended up Disabled.
                var endState = pipeline.DeploymentState == RtDeploymentStateEnum.Undeployed ||
                               pipeline.DeploymentState == RtDeploymentStateEnum.Disabled
                    ? (ruleSaysDisabled ? RtDeploymentStateEnum.Disabled : RtDeploymentStateEnum.Undeployed)
                    : pipeline.DeploymentState;
                if (endState == RtDeploymentStateEnum.Disabled)
                {
                    disabledPipelineRtIds.Add(pipeline.RtId);
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(ex,
                    "[{TenantId}] Failed to recompute deployment state for pipeline '{PipelineRtId}'",
                    tenantId, pipeline.RtId);
            }
        }

        // 4) Triggers: Disabled if no pipelines or every triggered pipeline is Disabled
        IDictionary<RtPipelineTrigger, IList<RtPipeline>> triggersAndPipelines;
        try
        {
            triggersAndPipelines = await _communicationRepository.GetTriggersAndPipelinesAsync(tenantId);
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "[{TenantId}] Failed to enumerate triggers during deployment-state recompute", tenantId);
            triggersAndPipelines = new Dictionary<RtPipelineTrigger, IList<RtPipeline>>();
        }

        foreach (var (trigger, triggeredPipelines) in triggersAndPipelines)
        {
            try
            {
                // Only flip resting states — a Deployed trigger has its cron schedule
                // live in the trigger management service and must be removed via the
                // proper Undeploy path.
                if (trigger.DeploymentState != RtDeploymentStateEnum.Undeployed &&
                    trigger.DeploymentState != RtDeploymentStateEnum.Disabled)
                {
                    continue;
                }

                var hasRunnablePipeline = triggeredPipelines.Any(p =>
                    !disabledPipelineRtIds.Contains(p.RtId));
                var ruleSaysDisabled = triggeredPipelines.Count == 0 || !hasRunnablePipeline;

                var target = ruleSaysDisabled
                    ? RtDeploymentStateEnum.Disabled
                    : RtDeploymentStateEnum.Undeployed;
                if (target != trigger.DeploymentState)
                {
                    await _communicationRepository.SetPipelineTriggerDeploymentStateAsync(tenantId, trigger.RtId,
                        target);
                    triggersUpdated++;
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(ex,
                    "[{TenantId}] Failed to recompute deployment state for trigger '{TriggerRtId}'",
                    tenantId, trigger.RtId);
            }
        }

        if (poolsUpdated + workloadsUpdated + pipelinesUpdated + triggersUpdated > 0)
        {
            await _eventService.StoreInformationEventAsync(tenantId,
                $"DeploymentState recompute: deploymentSites {poolsUpdated}, workloads {workloadsUpdated}, " +
                $"pipelines {pipelinesUpdated}, triggers {triggersUpdated} updated.");
        }

        Logger.Info(
            "[{TenantId}] Deployment-state recompute done: deploymentSites {DeploymentSites}, workloads {Workloads}, " +
            "pipelines {Pipelines}, triggers {Triggers}",
            tenantId, poolsUpdated, workloadsUpdated, pipelinesUpdated, triggersUpdated);
    }

    /// <summary>
    /// Computes the target DeploymentState for a workload at backfill time. Returns
    /// <c>null</c> when the current state must be left untouched — most notably for
    /// any operator-managed state (Deployed / Pending / Error), which reflects actual
    /// physical state and must not be silently overwritten. Only <c>Undeployed ↔
    /// Disabled</c> flips are allowed at backfill time. Anything operator-managed
    /// transitions to Disabled only via the Undeploy command path.
    /// </summary>
    private async Task<RtDeploymentStateEnum?> ComputeWorkloadTargetStateAsync(string tenantId,
        RtDeployableWorkload workload, RtDeploymentSite deploymentSite)
    {
        // Only touch resting states. Deployed/Pending/Error must stay — those reflect
        // real operator-managed resources in the cluster, regardless of whether the
        // missing-Helm rule currently says "should be Disabled".
        if (workload.DeploymentState != RtDeploymentStateEnum.Undeployed &&
            workload.DeploymentState != RtDeploymentStateEnum.Disabled)
        {
            return null;
        }

        // Edge deploymentSites are NOT a disabling rule for workloads (only for the deploymentSite
        // itself) — an edge operator deploys workloads via the same helm path
        // as the central operator. Only missing Helm fields disable a workload.
        _ = deploymentSite;

        return await IsWorkloadHelmDeployableAsync(tenantId, workload)
            ? RtDeploymentStateEnum.Undeployed
            : RtDeploymentStateEnum.Disabled;
    }

    /// <summary>
    ///     Validates the AB#4924 leasing configuration of a workload at deploy time.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Three shapes are checked, in the order a reader would ask about them: is this
    ///         workload allowed to be <c>Leased</c> at all, does it name a deploymentSite that actually lends
    ///         to it, and — for an <see cref="RtAdapterPool" /> — is the deploymentSite itself coherent.
    ///     </para>
    ///     <para>
    ///         🔴 The borrower half still has <b>no referential integrity behind it</b>. Since
    ///         AB#5271 the adapter's <c>LentFrom</c> edge is local and enforced, but what it points
    ///         at — the mirror's <c>Lender</c> record — names an <c>AdapterPool</c> in a different
    ///         tenant's database, where a CK association cannot reach. This method is still the only
    ///         thing in the system that can catch a half-configured or out-of-scope borrower before a
    ///         lease is attempted.
    ///     </para>
    /// </remarks>
    private async Task EnsureLeasingConfigurationIsValidAsync(string tenantId, RtDeployableWorkload workload)
    {
        var isLeased = workload.LifecycleMode == RtLifecycleModeEnum.Leased;

        if (workload is RtAdapterPool deploymentSite)
        {
            // A deploymentSite is never itself Leased. Leased means "has no process of its own"; a deploymentSite is
            // the thing that owns the processes. Catching this explicitly matters because the
            // concept text once described deploymentSite members as Leased, so it is a mistake an author is
            // actively invited to make.
            if (isLeased)
            {
                throw DeploymentSiteServiceException.AdapterPoolCannotBeLeased(tenantId, deploymentSite.RtId, deploymentSite.Name);
            }

            await EnsureAdapterPoolIsValidAsync(tenantId, deploymentSite);
            return;
        }

        // The borrower half lives on RtAdapter, not on RtDeployableWorkload: only an Adapter runs
        // pipelines, so only an Adapter can borrow a process to run them on. An Application that
        // somehow carries LifecycleMode=Leased is rejected below on exactly that basis.
        if (workload is not RtAdapter adapter)
        {
            if (isLeased)
            {
                throw DeploymentSiteServiceException.WorkloadLeasedNotSupportedForType(tenantId, workload.RtId, workload.Name);
            }

            return;
        }

        // AB#5271: the lender comes from the adapter's LentFrom edge to a borrower-local mirror.
        // Read once here and reused by every branch below — the edge is the same fact whether we are
        // about to reject it for being present, absent or out of scope.
        var mirror = await _communicationRepository.GetLentAdapterPoolForAdapterAsync(tenantId, adapter.RtId);

        if (!isLeased)
        {
            // A link that silently does nothing is worse than an error: a LentFrom edge on a workload
            // that runs its own process reads like it borrows one.
            if (mirror is not null)
            {
                throw DeploymentSiteServiceException.LentFromSetWithoutLeasedMode(tenantId, adapter.RtId, adapter.Name);
            }

            return;
        }

        // Same gate as OnDemand, and for the same reason: a lease is handed to a process BETWEEN
        // work items, so a process-bound trigger — which only fires while a process of its own is
        // running — can never be served by one. Reuses the AB#4984 classifier rather than
        // duplicating the trigger list, so the two modes can never drift apart.
        var capability = await _onDemandCapabilityService.EvaluateAsync(tenantId,
            new RtEntityId(SystemCommunicationCkIds.RtCkAdapterTypeId, adapter.RtId));
        if (!capability.IsCapable)
        {
            throw DeploymentSiteServiceException.WorkloadLeasedNotOnDemandCapable(tenantId, adapter.RtId, adapter.Name,
                capability.BlockingReasons);
        }

        if (mirror is null)
        {
            throw DeploymentSiteServiceException.LeasedWorkloadWithoutLender(tenantId, adapter.RtId, adapter.Name);
        }

        // The edge exists, so the borrower does name a pool — but the mirror it names can still carry
        // a Lender record that says nothing usable. The provisioning sweep writes that record as one
        // unit, so this only happens when somebody edited the mirror in the borrower's own database.
        var lentFrom = LentFromReference.FromMirror(mirror);
        if (lentFrom is null)
        {
            throw DeploymentSiteServiceException.LeasedWorkloadLenderIncomplete(tenantId, adapter.RtId, adapter.Name);
        }

        var lenderTenantId = lentFrom.LenderTenantId;

        // Resolve the deploymentSite in the LENDING tenant and ask whether it lends here. Both halves are
        // needed: a deploymentSite that does not exist, and a deploymentSite that exists but whose SharingMode or
        // allow-list excludes this tenant, are different misconfigurations with the same symptom.
        //
        // 🔴 Read from the LENDER, never from the mirror. The mirror carries a SharingMode of its own
        // and a borrower can edit its own database — deciding from it would let any tenant grant
        // itself a pool it was never lent.
        var lendingScope = await _communicationRepository
            .TryGetAdapterPoolLendingScopeAsync(lenderTenantId, lentFrom.AdapterPoolRtId);
        if (lendingScope is null ||
            !await _lendingScopeResolver.MayLendAsync(lenderTenantId, tenantId, lendingScope.Value))
        {
            throw DeploymentSiteServiceException.LenderDoesNotLendToThisTenant(tenantId, adapter.RtId, adapter.Name,
                lenderTenantId);
        }
    }

    /// <summary>
    ///     Validates an <see cref="RtAdapterPool" />'s own configuration (AB#4924 §4a).
    /// </summary>
    private async Task EnsureAdapterPoolIsValidAsync(string tenantId, RtAdapterPool deploymentSite)
    {
        if (deploymentSite.MinReplicas < 0 || deploymentSite.MaxReplicas < 1 || deploymentSite.MaxReplicas < deploymentSite.MinReplicas)
        {
            throw DeploymentSiteServiceException.AdapterPoolReplicaRangeInvalid(tenantId, deploymentSite.RtId, deploymentSite.Name,
                deploymentSite.MinReplicas, deploymentSite.MaxReplicas);
        }

        if (deploymentSite.LendingMaxConcurrentLeasesPerTenant is { } cap && cap < 1)
        {
            // Unset means "no per-tenant cap" and is the documented default (concept §8, Q11).
            // Zero is not that — it is a cap that can never be satisfied, so work would queue
            // forever with no error anywhere.
            throw DeploymentSiteServiceException.AdapterPoolLeaseCapInvalid(tenantId, deploymentSite.RtId, deploymentSite.Name, cap);
        }

        if (deploymentSite.SharingMode == RtAdapterSharingModeEnum.NotShared)
        {
            return;
        }

        // A deploymentSite that lends must be able to hand its process to a borrower between work items,
        // which is the same requirement OnDemand makes of a workload.
        var capability = await _onDemandCapabilityService.EvaluateAsync(tenantId,
            new RtEntityId(SystemCommunicationCkIds.RtCkAdapterPoolTypeId, deploymentSite.RtId));
        if (!capability.IsCapable)
        {
            throw DeploymentSiteServiceException.AdapterPoolNotOnDemandCapable(tenantId, deploymentSite.RtId, deploymentSite.Name);
        }
    }

    /// <summary>
    /// Non-throwing companion to <see cref="EnsureWorkloadIsHelmDeployableAsync"/>: returns
    /// <c>true</c> iff the workload has all required Helm fields (ChartName, an associated
    /// HelmRepositoryConfiguration with a non-empty RepositoryUrl). <c>ChartVersion</c> is
    /// optional and an empty value is valid (= "use latest"), so it does not gate this check.
    /// Used by the backfill to classify entities without throwing.
    /// </summary>
    /// <summary>
    ///     The Helm repository a workload pulls its chart from (AB#5295).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         An explicit <c>HelmRepository</c> association always wins. Without one, the tenant's
    ///         repositories are filtered by <c>Purpose</c> against the workload type — an
    ///         <c>Adapter</c> or <c>AdapterPool</c> resolves to the tenant's <c>Adapters</c>
    ///         repository, an <c>Application</c> to its <c>Applications</c> repository — and the
    ///         fallback applies only when that leaves <b>exactly one</b> candidate. Zero or several
    ///         resolve to null, which every caller already treats as "no repository linked".
    ///     </para>
    ///     <para>
    ///         🔴 Why a modelled purpose and not the well-known name: on the release channel adapter
    ///         charts and tenant-app charts live in two different indexes (<c>github.io/charts</c>
    ///         vs <c>github.io/apps</c>), so "the tenant's repository" is ambiguous there, and the
    ///         only thing that used to tell them apart was the <c>rtWellKnownName</c> convention. A
    ///         repository that declares no purpose is therefore never chosen — tenants seeded
    ///         before <c>System.Communication</c> 4.1.0 keep their exact previous behaviour until
    ///         the channel blueprint stamps the attribute.
    ///     </para>
    ///     <para>
    ///         This exists so an app blueprint no longer has to pin a channel-specific repository
    ///         rtId in its seed: the accounting blueprint pinned the DEV adapter repository on
    ///         Release tenants, which on prod-1 failed the operator's dry-run and left dangling
    ///         edges that blocked the correct one (AB#5295).
    ///     </para>
    /// </remarks>
    private async Task<RtHelmRepositoryConfiguration?> ResolveHelmRepositoryAsync(string tenantId,
        RtDeployableWorkload workload)
    {
        var explicitRepo = await _communicationRepository.GetHelmRepositoryForWorkloadAsync(tenantId, workload.RtId);
        if (explicitRepo is not null)
        {
            return explicitRepo;
        }

        var wanted = workload is RtApplication
            ? RtHelmRepositoryPurposeEnum.Applications
            : RtHelmRepositoryPurposeEnum.Adapters;

        var candidates = (await _communicationRepository.GetHelmRepositoryConfigurationsAsync(tenantId))
            .Where(r => ReadPurpose(r) == wanted)
            .ToList();

        if (candidates.Count != 1)
        {
            if (candidates.Count > 1)
            {
                Logger.Warn(
                    "[{TenantId}] Workload '{WorkloadName}' ({WorkloadRtId}) has no HelmRepository association and " +
                    "{Count} repositories declare purpose {Purpose} — refusing to guess; link one explicitly",
                    tenantId, workload.Name, workload.RtId, candidates.Count, wanted);
            }

            return null;
        }

        Logger.Info(
            "[{TenantId}] Workload '{WorkloadName}' ({WorkloadRtId}) has no HelmRepository association; using the " +
            "tenant's {Purpose} repository '{RepositoryName}' ({RepositoryUrl})",
            tenantId, workload.Name, workload.RtId, wanted,
            candidates[0].RtWellKnownName ?? candidates[0].RtId.ToString(), candidates[0].RepositoryUrl);
        return candidates[0];
    }

    /// <summary>
    ///     <c>Purpose</c> of a repository, or null when it declares none.
    /// </summary>
    /// <remarks>
    ///     Read through <c>GetAttributeValueOrDefault</c> rather than the generated property: the
    ///     attribute is optional and new in 4.1.0, and a repository written before it existed must
    ///     read as "no purpose" rather than throw inside a deploy.
    /// </remarks>
    private static RtHelmRepositoryPurposeEnum? ReadPurpose(RtHelmRepositoryConfiguration repository)
    {
        var raw = repository.GetAttributeValueOrDefault(nameof(RtHelmRepositoryConfiguration.Purpose));
        return raw switch
        {
            RtHelmRepositoryPurposeEnum e => e,
            int i when Enum.IsDefined(typeof(RtHelmRepositoryPurposeEnum), i) => (RtHelmRepositoryPurposeEnum)i,
            long l when Enum.IsDefined(typeof(RtHelmRepositoryPurposeEnum), (int)l) => (RtHelmRepositoryPurposeEnum)(int)l,
            _ => null
        };
    }

    private async Task<bool> IsWorkloadHelmDeployableAsync(string tenantId, RtDeployableWorkload workload)
    {
        if (string.IsNullOrWhiteSpace(workload.ChartName)) return false;

        var repo = await ResolveHelmRepositoryAsync(tenantId, workload);
        if (repo == null) return false;
        return !string.IsNullOrWhiteSpace(repo.RepositoryUrl);
    }

    /// <inheritdoc />
    public async Task RestoreDeployedStateAsync(string operatorConnectionId,
        IReadOnlyList<OperatorDeployedDeploymentSiteReportDto> deployedDeploymentSites)
    {
        // Defensive: an empty list is a valid no-op (operator just restarted
        // and currently owns nothing). Don't log noise.
        if (deployedDeploymentSites.Count == 0)
        {
            return;
        }

        Logger.Info(
            "Reverse-sync from operator connection '{ConnectionId}': {Count} deploymentSite report(s)",
            operatorConnectionId, deployedDeploymentSites.Count);

        foreach (var report in deployedDeploymentSites)
        {
            // Load by repository — the deploymentSite may or may not be in the local
            // cache yet (operator can call ReportDeployedStateAsync before
            // any RegisterDeploymentSiteAsync for the same deploymentSite has been processed).
            var deploymentSites = await _communicationRepository.GetDeploymentSitesAsync(report.TenantId);
            var rtDeploymentSite = deploymentSites.FirstOrDefault(p => p.RtId.ToString() == report.DeploymentSiteRtId);
            if (rtDeploymentSite == null)
            {
                Logger.Warn(
                    "[{TenantId}] Reverse-sync: deployment site rtId '{DeploymentSiteRtId}' reported by operator does not exist; skipping",
                    report.TenantId, report.DeploymentSiteRtId);
                continue;
            }

            // Per-deploymentSite environment guard: a Cloud operator (mode check ran in
            // OperatorHub) must not be able to revive Edge-deploymentSite state via this
            // path. Mirrors the same enforcement on RegisterDeploymentSiteAsync.
            if (rtDeploymentSite.Environment != RtEnvironmentEnum.Cloud)
            {
                Logger.Warn(
                    "[{TenantId}] Reverse-sync: deploymentSite '{DeploymentSiteName}' has Environment={Environment} (not Cloud); skipping",
                    report.TenantId, rtDeploymentSite.Name, rtDeploymentSite.Environment);
                continue;
            }

            // Only write when state would actually change — avoids no-op
            // SetState calls firing audit events for every report.
            if (rtDeploymentSite.DeploymentState != RtDeploymentStateEnum.Deployed)
            {
                await _communicationRepository.SetDeploymentSiteDeploymentStateAsync(report.TenantId, rtDeploymentSite.RtId,
                    RtDeploymentStateEnum.Deployed);
                await _eventService.StoreInformationEventAsync(report.TenantId,
                    $"DeploymentSite '{rtDeploymentSite.Name}' DeploymentState restored to Deployed by operator reverse-sync " +
                    $"(was {rtDeploymentSite.DeploymentState}).",
                    new RtEntityId(SystemCommunicationCkIds.RtCkDeploymentSiteTypeId, rtDeploymentSite.RtId));
                Logger.Info(
                    "[{TenantId}] Reverse-sync: deploymentSite '{DeploymentSiteName}' restored to Deployed (was {OldState})",
                    report.TenantId, rtDeploymentSite.Name, rtDeploymentSite.DeploymentState);
            }

            // Always rebuild the tracking + per-connection deploymentSite registration —
            // they're keyed on the new connection id, and the previous
            // connection's entries were dropped on disconnect. Idempotent if
            // the connection is already registered (ConcurrentDictionary set).
            _operatorConnectionManager.TrackDeployedDeploymentSite(new DeployedDeploymentSiteDto
            {
                TenantId = report.TenantId,
                DeploymentSiteRtId = report.DeploymentSiteRtId,
            });
            _operatorConnectionManager.RegisterDeploymentSiteForConnection(operatorConnectionId, report.TenantId,
                report.DeploymentSiteRtId);

            // Workloads inside the deploymentSite — same restore-only-when-changed rule.
            foreach (var workloadRtIdString in report.WorkloadRtIds)
            {
                if (!OctoObjectId.TryParse(workloadRtIdString, out var workloadRtId))
                {
                    Logger.Warn(
                        "[{TenantId}] Reverse-sync: workload rtId '{RtId}' under deploymentSite '{DeploymentSiteName}' is not a valid OctoObjectId; skipping",
                        report.TenantId, workloadRtIdString, rtDeploymentSite.Name);
                    continue;
                }

                var workload = await _communicationRepository.GetWorkloadByRtIdAsync(report.TenantId, workloadRtId);
                if (workload == null)
                {
                    Logger.Warn(
                        "[{TenantId}] Reverse-sync: workload rtId '{RtId}' reported by operator under deploymentSite '{DeploymentSiteName}' does not exist; skipping",
                        report.TenantId, workloadRtIdString, rtDeploymentSite.Name);
                    continue;
                }

                if (workload.DeploymentState != RtDeploymentStateEnum.Deployed)
                {
                    await SetWorkloadDeploymentStateAsync(report.TenantId, workload, RtDeploymentStateEnum.Deployed);
                    await _eventService.StoreInformationEventAsync(report.TenantId,
                        $"Workload '{workload.Name}' DeploymentState restored to Deployed by operator reverse-sync " +
                        $"(was {workload.DeploymentState}).");
                    Logger.Info(
                        "[{TenantId}] Reverse-sync: workload '{WorkloadName}' restored to Deployed (was {OldState})",
                        report.TenantId, workload.Name, workload.DeploymentState);
                }

                // Rebuild workload tracking so PreDeleteTenant cascade can fan
                // out undeploy events for restored workloads. Same minimal
                // DTO shape NotifyWorkloadDeployedAsync stores.
                _operatorConnectionManager.TrackDeployedWorkload(new WorkloadUndeployedDto
                {
                    TenantId = report.TenantId,
                    DeploymentSiteRtId = report.DeploymentSiteRtId,
                    WorkloadRtId = workloadRtIdString,
                    WorkloadName = workload.Name ?? string.Empty,
                    WorkloadType = WorkloadWireMapping.ResolveWorkloadType(workload),
                });
            }
        }
    }
}