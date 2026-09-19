using Meshmakers.Octo.Backend.CommunicationControllerServices.Models;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;

/// <summary>
/// Repository for pool related operations
/// </summary>
public interface ICommunicationRepository
{
    /// <summary>
    /// Get all communication adapter from a pool
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="poolRtId">Object id of pool</param>
    /// <returns></returns>
    Task<IReadOnlyCollection<RtAdapter>> GetAdaptersAsync(string tenantId, OctoObjectId poolRtId);

    /// <summary>
    /// Gets all deployable workloads (Adapters + Applications) managed by the
    /// given pool. Returned as the abstract <c>RtDeployableWorkload</c> base
    /// so callers can iterate uniformly; the concrete type is preserved in
    /// each item's <c>CkTypeId</c>.
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="poolRtId">Object id of pool</param>
    Task<IReadOnlyCollection<RtDeployableWorkload>> GetWorkloadsForDeploymentSiteAsync(string tenantId, OctoObjectId poolRtId);

    /// <summary>
    /// Loads every deployable workload (Adapter and Application) of the tenant, regardless of
    /// whether a pool manages it. Polymorphic like <see cref="GetWorkloadsForDeploymentSiteAsync"/>.
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    Task<IReadOnlyCollection<RtDeployableWorkload>> GetWorkloadsAsync(string tenantId);

    /// <summary>
    /// Loads a single deployable workload by runtime id. Returns
    /// <c>null</c> when no entity with that id exists.
    /// </summary>
    Task<RtDeployableWorkload?> GetWorkloadByRtIdAsync(string tenantId, OctoObjectId workloadRtId);

    /// <summary>
    /// Writes the workload's lifecycle state (AB#4914) and optionally its
    /// <c>StatusMessage</c>. Polymorphic over Adapter / Application — the
    /// entity is loaded first to resolve the concrete CK type. Throws when
    /// the workload does not exist.
    /// </summary>
    Task SetWorkloadLifecycleStateAsync(string tenantId, OctoObjectId workloadRtId,
        RtLifecycleStateEnum lifecycleState, string? statusMessage = null);

    /// <summary>
    /// Stamps the workload's <c>LastActivityAt</c> (AB#4914) — input to the
    /// idle watchdog. Polymorphic over Adapter / Application. Throws when
    /// the workload does not exist.
    /// </summary>
    Task SetWorkloadLastActivityAsync(string tenantId, OctoObjectId workloadRtId, DateTime lastActivityAtUtc);

    /// <summary>
    /// Writes the workload's computed <c>OnDemandCapable</c> flag and
    /// <c>OnDemandBlockingReasons</c> (AB#4984) — display aid for the Studio
    /// next to the LifecycleMode setting. Polymorphic over Adapter /
    /// Application. Throws when the workload does not exist.
    /// </summary>
    Task SetWorkloadOnDemandCapabilityAsync(string tenantId, OctoObjectId workloadRtId, bool onDemandCapable,
        string? blockingReasons);

    /// <summary>
    /// Walks the <c>Hosts</c> association from a workload back to its
    /// parent <c>RtDeploymentSite</c>. Returns <c>null</c> when the workload is not
    /// currently in any pool.
    /// </summary>
    /// <summary>
    ///     Reads the lending configuration of an <c>AdapterPool</c> that lives in another tenant
    ///     (AB#4924). Returns null when the tenant, the RtId or the pool cannot be resolved — the
    ///     caller treats "no such pool" and "does not lend here" identically.
    /// </summary>
    Task<LendingScope?> TryGetAdapterPoolLendingScopeAsync(string lenderTenantId, string poolRtId);

    /// <summary>
    ///     Every <c>AdapterPool</c> in <paramref name="lenderTenantId" />, with the fields a
    ///     borrower's mirror carries (AB#5271).
    /// </summary>
    /// <remarks>
    ///     Reads a tenant the caller is usually not in — the same cross-tenant read as
    ///     <see cref="TryGetAdapterPoolLendingScopeAsync" />, widened from one known pool to all of
    ///     them, because mirroring has to discover pools rather than resolve a named one.
    ///
    ///     <para>
    ///     🔴 Returns pools regardless of their sharing mode. Filtering by who may borrow is the
    ///     caller's job via <c>MayLendAsync</c>, and deliberately not folded in here: the scope
    ///     check needs the BORROWER's id, which this method has no business knowing, and keeping
    ///     the two apart is what lets one read serve every borrower in a fan-out.
    ///     </para>
    /// </remarks>
    Task<IReadOnlyCollection<LendableAdapterPool>> GetAdapterPoolsForMirroringAsync(string lenderTenantId);

    /// <summary>
    ///     One adapter pool of <paramref name="lenderTenantId" />, read in full so a BORROWER can
    ///     inspect it (AB#5271).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         🔴 <b>The live counterpart of <see cref="GetAdapterPoolsForMirroringAsync" />, and the
    ///         reason the mirror stays small.</b> Chart, sizing and scale-up policy are the lender's
    ///         deployment detail: copying them into every borrower's database would multiply the
    ///         places they can go stale and would persist one tenant's configuration inside another.
    ///         Read here instead, per request, and never written down.
    ///     </para>
    ///     <para>
    ///         🔴 Returns the pool regardless of its sharing mode, exactly like
    ///         <see cref="GetAdapterPoolsForMirroringAsync" />: whether the asking tenant may see it
    ///         is decided by the caller through <c>MayLendAsync</c> against the returned
    ///         <c>Scope</c>. Reading it here is not permission to show it.
    ///     </para>
    ///     <para>
    ///         Null for every "cannot resolve" case — unparsable id, tenant gone, pool gone, read
    ///         failed — because the caller answers all four the same way: no pool lends here under
    ///         that id.
    ///     </para>
    /// </remarks>
    Task<AdapterPoolDetails?> TryGetAdapterPoolDetailsAsync(string lenderTenantId, string adapterPoolRtId);

    /// <summary>
    ///     The <c>LentAdapterPool</c> mirrors currently stored in <paramref name="borrowerTenantId" />.
    /// </summary>
    Task<IReadOnlyCollection<RtLentAdapterPool>> GetLentAdapterPoolMirrorsAsync(string borrowerTenantId);

    /// <summary>
    ///     The mirror one borrowing adapter points at through its <c>LentFrom</c> edge, or null when
    ///     it points at none (AB#5271).
    /// </summary>
    Task<RtLentAdapterPool?> GetLentAdapterPoolForAdapterAsync(string borrowerTenantId, OctoObjectId adapterRtId);

    /// <summary>
    ///     The same resolution for many adapters at once, keyed by adapter RtId; adapters with no
    ///     <c>LentFrom</c> edge are absent from the result (AB#5271).
    /// </summary>
    /// <remarks>
    ///     The batch form exists for the lease-topology sweep, which walks every leased adapter of
    ///     every enabled tenant every few seconds. Resolving one adapter at a time there would turn
    ///     one query per tenant into one per borrower.
    /// </remarks>
    Task<IReadOnlyDictionary<OctoObjectId, RtLentAdapterPool>> GetLentAdapterPoolsForAdaptersAsync(
        string borrowerTenantId, IReadOnlyCollection<OctoObjectId> adapterRtIds);

    /// <summary>
    ///     Creates or updates the mirror of one lent pool in the borrower, and reports whether
    ///     anything was written.
    /// </summary>
    /// <remarks>
    ///     Idempotent: matched on the <c>Lender</c> record, so a second call with the same lender
    ///     and pool updates the existing mirror instead of adding a second one — and a mirror that
    ///     already says the right thing is left untouched, so a reconcile over an unchanged estate
    ///     writes nothing and reports nothing.
    /// </remarks>
    Task<LentAdapterPoolMirrorUpsert> UpsertLentAdapterPoolMirrorAsync(string borrowerTenantId,
        LendableAdapterPool pool);

    /// <summary>
    ///     Removes a mirror that no longer corresponds to a pool this tenant may borrow.
    /// </summary>
    /// <remarks>
    ///     🔴 Removing the mirror must not cascade into the borrower's adapters. An adapter left
    ///     pointing at nothing is a defined, reported state — a refused deploy with a named reason
    ///     (concept §6, "parent tenant deleted while lending"). Deleting a tenant's adapters
    ///     because a lender narrowed its scope would be a far worse failure than a blocked deploy.
    /// </remarks>
    Task RemoveLentAdapterPoolMirrorAsync(string borrowerTenantId, OctoObjectId mirrorRtId);

    /// <summary>
    ///     Returns the deployment site a workload is hosted at, or null when it is not assigned.
    /// </summary>
    Task<RtDeploymentSite?> GetDeploymentSiteForWorkloadAsync(string tenantId, OctoObjectId workloadRtId);

    /// <summary>
    /// Resolves the <c>HelmRepositoryConfiguration</c> referenced by a
    /// deployable workload via its <c>Uses</c> association. Returns
    /// <c>null</c> when the workload does not yet have a repository
    /// associated.
    /// </summary>
    Task<RtHelmRepositoryConfiguration?> GetHelmRepositoryForWorkloadAsync(string tenantId,
        OctoObjectId workloadRtId);

    /// <summary>
    /// AB#5027: resolves the <c>ServiceAccountConfiguration</c> linked to an adapter through the
    /// dedicated <c>PipelineServiceAccount</c> association role. This is the adapter-wide default
    /// identity every pipeline of that adapter executes as, unless the pipeline carries its own
    /// service account on the generic <c>Uses</c> role. Returns <c>null</c> when the adapter has
    /// no service account linked (the model multiplicity is ZeroOrOne — the obligation is
    /// enforced by the deploy guard, not by the model).
    /// </summary>
    Task<RtServiceAccountConfiguration?> GetServiceAccountForAdapterAsync(string tenantId,
        OctoObjectId adapterRtId);

    /// <summary>
    /// AB#5027: looks a <c>ServiceAccountConfiguration</c> up by its <c>RtWellKnownName</c>.
    /// The provisioning path derives a deterministic well-known name per adapter, so this is what
    /// makes a second provisioning run recognise its own earlier work — including the case where
    /// the entity exists but the <c>PipelineServiceAccount</c> edge was lost (a half-applied run,
    /// or an operator who unlinked it). Returns <c>null</c> when no such entity exists.
    /// </summary>
    Task<RtServiceAccountConfiguration?> GetServiceAccountByWellKnownNameAsync(string tenantId,
        string wellKnownName);

    /// <summary>
    /// AB#5111: looks a <c>ServiceAccountConfiguration</c> up by its rtId — the handle the
    /// configuration-bound reconcile/rotate endpoints receive. Returns <c>null</c> when no such
    /// entity exists (or the entity with that rtId is not a service account configuration).
    /// </summary>
    Task<RtServiceAccountConfiguration?> GetServiceAccountByRtIdAsync(string tenantId,
        OctoObjectId serviceAccountRtId);

    /// <summary>
    /// AB#5111: the reverse of <see cref="GetServiceAccountForAdapterAsync" /> — the adapter whose
    /// <c>PipelineServiceAccount</c> edge points at the given configuration, or <c>null</c> for a
    /// standalone configuration (e.g. a per-pipeline override linked only via <c>Uses</c>). Lets
    /// the configuration-bound reconcile/rotate operations route through the adapter path, so both
    /// entry points share one deterministic naming scheme per adapter.
    /// </summary>
    Task<RtAdapter?> GetAdapterForServiceAccountAsync(string tenantId, OctoObjectId serviceAccountRtId);

    /// <summary>
    /// AB#5111: updates a standalone <c>ServiceAccountConfiguration</c> in place — the counterpart
    /// of <see cref="SavePipelineServiceAccountAsync" /> for configurations that belong to no
    /// adapter, so no <c>PipelineServiceAccount</c> edge is touched.
    /// <para>
    /// 🔴 <paramref name="serviceAccount"/> carries a plaintext client secret. Never log this
    /// parameter, and never include it in an exception message.
    /// </para>
    /// </summary>
    Task UpdateServiceAccountAsync(string tenantId, RtServiceAccountConfiguration serviceAccount);

    /// <summary>
    /// AB#5027: inserts or updates the given <c>ServiceAccountConfiguration</c> and makes sure the
    /// adapter's <c>PipelineServiceAccount</c> edge points at it — both in one transaction, so a
    /// provisioning run can never leave an orphaned credential entity behind.
    /// <para>
    /// The edge write is itself idempotent: an already existing edge to the same target is left
    /// alone (re-inserting it would violate the ZeroOrOne outbound multiplicity), and an edge that
    /// points at a *different* service account is replaced — the adapter has exactly one default.
    /// </para>
    /// <para>
    /// 🔴 <paramref name="serviceAccount"/> carries a plaintext client secret. Never log this
    /// parameter, and never include it in an exception message.
    /// </para>
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="adapterRtEntityId">The adapter that owns the service account</param>
    /// <param name="serviceAccount">
    ///     The configuration to persist. Its <c>RtId</c> must be the existing entity's id when
    ///     <paramref name="isNewEntity"/> is <c>false</c>.
    /// </param>
    /// <param name="isNewEntity">
    ///     <c>true</c> to insert, <c>false</c> to update the entity carrying
    ///     <c>serviceAccount.RtId</c>. The caller knows which, because it had to read the entity to
    ///     decide whether a secret must be issued at all.
    /// </param>
    Task SavePipelineServiceAccountAsync(string tenantId, RtEntityId adapterRtEntityId,
        RtServiceAccountConfiguration serviceAccount, bool isNewEntity);

    /// <summary>
    /// AB#5143: every <c>SignalChannel</c> entity of the tenant. By invariant at most one exists
    /// (singleton per tenant, enforced by <c>SignalChannelService</c>); the list shape keeps the
    /// repository honest about what is actually stored.
    /// </summary>
    Task<IReadOnlyCollection<RtSignalChannel>> GetSignalChannelsAsync(string tenantId);

    /// <summary>
    /// AB#5143: inserts or updates the tenant's <c>SignalChannel</c> definition. The singleton and
    /// cross-tenant number-claim invariants live in <c>SignalChannelService</c>, not here.
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="signalChannel">The channel to persist.</param>
    /// <param name="isNewEntity">
    ///     <c>true</c> to insert, <c>false</c> to update the entity carrying
    ///     <c>signalChannel.RtId</c>.
    /// </param>
    Task SaveSignalChannelAsync(string tenantId, RtSignalChannel signalChannel, bool isNewEntity);

    /// <summary>
    /// AB#5143: deletes the tenant's <c>SignalChannel</c> definition for real (Erase, no archive
    /// tombstone) — the stored definitions are the instance-wide number-claim registry, and an
    /// archived copy must not keep a number blocked.
    /// </summary>
    Task DeleteSignalChannelAsync(string tenantId, RtEntityId signalChannelRtEntityId);

    /// <summary>
    /// Lists every <see cref="RtDeployableWorkload"/> in the tenant whose
    /// <c>ChartName</c> equals <paramref name="chartName"/>. Returns an
    /// empty collection when the chart is not used in this tenant — the
    /// CI/CD rollout flow (Epic 3054) uses this to skip tenants silently.
    /// </summary>
    Task<IReadOnlyCollection<RtDeployableWorkload>> GetWorkloadsByChartNameAsync(string tenantId,
        string chartName);

    /// <summary>
    /// Sets <c>ChartVersion</c> on a single workload. Returns the previous
    /// version so callers can log a meaningful audit event. Throws when
    /// the workload does not exist.
    /// </summary>
    /// <returns>The chart version the workload had before the update.</returns>
    Task<string?> UpdateWorkloadChartVersionAsync(string tenantId, OctoObjectId workloadRtId,
        string newChartVersion);

    /// <summary>
    /// Gets a list of initialized communication adapter of the given tenant
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <returns></returns>
    Task<IReadOnlyCollection<RtAdapter>> GetAdaptersAsync(string tenantId);

    /// <summary>
    /// Gets an adapter by object id
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="adapterRtEntityId">ID of adapter</param>
    /// <returns></returns>
    Task<RtAdapter> GetAdapterAsync(string tenantId, RtEntityId adapterRtEntityId);

    /// <summary>
    /// Gets an adapter by his pipeline
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="pipelineRtEntityId">Object identifier of pipeline</param>
    /// <returns></returns>
    Task<RtAdapter?> GetAdapterByPipelineAsync(string tenantId, RtEntityId pipelineRtEntityId);

    /// <summary>
    ///     Reassigns a pipeline from its current adapter to <paramref name="targetAdapterRtId"/>
    ///     by swapping the <c>Pipeline.Executes</c> association atomically
    ///     (delete + insert in a single transaction).
    ///
    ///     Validates that the current and target adapters carry the exact
    ///     same <c>CkTypeId</c> — moving a pipeline onto an adapter of a
    ///     different concrete subtype is rejected to avoid landing nodes on
    ///     an adapter that cannot execute them. When the pipeline already
    ///     points at <paramref name="targetAdapterRtId"/>, the call is a
    ///     no-op and returns the unchanged state.
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="pipelineRtId">Runtime id of the pipeline to move</param>
    /// <param name="targetAdapterRtId">Runtime id of the new owning adapter</param>
    /// <returns>The pipeline id together with the old and new adapter ids.</returns>
    Task<PipelineMoveResult> MovePipelineToAdapterAsync(string tenantId, OctoObjectId pipelineRtId,
        OctoObjectId targetAdapterRtId);

    /// <summary>
    /// Get pools for a tenant
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <returns>List of pools of the tenant</returns>
    Task<IReadOnlyCollection<RtDeploymentSite>> GetDeploymentSitesAsync(string tenantId);


    /// <summary>
    /// Get pools for a tenant by name
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="deploymentSiteName">Name of the pool</param>
    /// <returns>List of pools with the given name</returns>
    Task<IReadOnlyCollection<RtDeploymentSite>> GetPoolByNameAsync(string tenantId, string deploymentSiteName);

    /// <summary>
    /// Creates a pool
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="deploymentSiteName">Name of pool</param>
    /// <exception cref="CommunicationRepositoryException"></exception>
    Task CreatePoolAsync(string tenantId, string deploymentSiteName);

    /// <summary>
    /// Set the deployment state of a pool
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="poolRtId">Object id of pool</param>
    /// <param name="deploymentState">State of pool</param>
    /// <returns></returns>
    Task SetDeploymentSiteDeploymentStateAsync(string tenantId, OctoObjectId poolRtId, RtDeploymentStateEnum deploymentState);

    /// <summary>
    /// Set the communication state of a pool
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="poolRtId">Object id of pool</param>
    /// <param name="communicationState">State of pool</param>
    /// <returns></returns>
    Task SetDeploymentSiteCommunicationStateAsync(string tenantId, OctoObjectId poolRtId,
        RtCommunicationStateEnum communicationState);

    /// <summary>
    /// Set the deployment state of an adapter
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="adapterRtEntityId">Object id of adapter</param>
    /// <param name="deploymentState">State of adapter</param>
    /// <param name="stateMessage">Optional human-readable status message, written to <c>StatusMessage</c>.</param>
    /// <returns></returns>
    Task SetAdapterDeploymentStateAsync(string tenantId, RtEntityId adapterRtEntityId,
        RtDeploymentStateEnum deploymentState, string? stateMessage = null);

    /// <summary>
    /// Set the deployment state of an adapter
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="adapterRtEntityIds">Object id of adapters</param>
    /// <param name="deploymentState">State of adapter</param>
    /// <param name="stateMessage">Optional human-readable status message, written to <c>StatusMessage</c>.</param>
    /// <returns></returns>
    Task SetAdapterDeploymentStateAsync(string tenantId, ICollection<RtEntityId> adapterRtEntityIds,
        RtDeploymentStateEnum deploymentState, string? stateMessage = null);

    /// <summary>
    /// Set the communication state of a communication adapter
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="adapterRtEntityId">Object id of adapter</param>
    /// <param name="communicationState">State of adapter</param>
    /// <returns></returns>
    Task SetAdapterCommunicationStateAsync(string tenantId, RtEntityId adapterRtEntityId,
        RtCommunicationStateEnum communicationState);

    /// <summary>
    /// Set the deployment state of an application
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="applicationRtEntityId">Object id of application</param>
    /// <param name="deploymentState">State of application</param>
    /// <param name="stateMessage">Optional human-readable status message, written to <c>StatusMessage</c>.</param>
    /// <returns></returns>
    Task SetApplicationDeploymentStateAsync(string tenantId, RtEntityId applicationRtEntityId,
        RtDeploymentStateEnum deploymentState, string? stateMessage = null);

    /// <summary>
    /// Set the deployment state of one or more applications in a single transaction.
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="applicationRtEntityIds">Object ids of applications</param>
    /// <param name="deploymentState">State of applications</param>
    /// <param name="stateMessage">Optional human-readable status message, written to <c>StatusMessage</c>.</param>
    /// <returns></returns>
    Task SetApplicationDeploymentStateAsync(string tenantId, ICollection<RtEntityId> applicationRtEntityIds,
        RtDeploymentStateEnum deploymentState, string? stateMessage = null);

    /// <summary>
    /// Set the deployment state of an adapter pool (AB#4924). A pool is a
    /// <c>DeployableWorkload</c> like Adapter and Application and goes through the same
    /// deploy / undeploy path, so it needs its own writer — <c>EntityUpdateInfo</c> is typed on the
    /// concrete CK type and there is no common base to write through.
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="adapterPoolRtEntityId">Object id of the adapter pool</param>
    /// <param name="deploymentState">State of the adapter pool</param>
    /// <param name="stateMessage">Optional human-readable status message, written to <c>StatusMessage</c>.</param>
    Task SetAdapterPoolDeploymentStateAsync(string tenantId, RtEntityId adapterPoolRtEntityId,
        RtDeploymentStateEnum deploymentState, string? stateMessage = null);

    /// <summary>
    /// Set the deployment state of one or more adapter pools in a single transaction (AB#4924).
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="adapterPoolRtEntityIds">Object ids of the adapter pools</param>
    /// <param name="deploymentState">State of the adapter pools</param>
    /// <param name="stateMessage">Optional human-readable status message, written to <c>StatusMessage</c>.</param>
    Task SetAdapterPoolDeploymentStateAsync(string tenantId, ICollection<RtEntityId> adapterPoolRtEntityIds,
        RtDeploymentStateEnum deploymentState, string? stateMessage = null);

    /// <summary>
    /// Gets the pool of a communication adapter
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="adapterRtEntityId">Adapter id</param>
    /// <returns></returns>
    Task<RtDeploymentSite> GetPoolOfAdapterAsync(string tenantId, RtEntityId adapterRtEntityId);

    /// <summary>
    /// Returns true if a tenant exists
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <returns></returns>
    Task<bool> IsTenantExistingAsync(string tenantId);

    /// <summary>
    /// Gets the pipelines of a communication adapter
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="adapterRtEntityId">Object identifier of communication adapter</param>
    /// <returns></returns>
    Task<IReadOnlyCollection<RtPipeline>> GetPipelinesAsync(string tenantId, RtEntityId adapterRtEntityId);

    /// <summary>
    /// Gets the pipelines of a data flow
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="dataFlowRtId">Object identifier of data flow</param>
    /// <returns></returns>
    Task<IReadOnlyCollection<RtPipeline>> GetPipelinesAsync(string tenantId, OctoObjectId dataFlowRtId);

    /// <summary>
    /// Get the pipeline by id
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="pipelineRtEntityId">Object identifier of pipeline</param>
    /// <returns></returns>
    Task<RtPipeline?> GetPipelineAsync(string tenantId, RtEntityId pipelineRtEntityId);

    /// <summary>
    /// Get the data flow based on the child pipeline
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="pipelineRtId">Object identifier of pipeline</param>
    /// <returns></returns>
    Task<RtDataFlow?> GetDataFlowByPipelineAsync(string tenantId, OctoObjectId pipelineRtId);

    /// <summary>
    /// Get configurations of a pipeline
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="pipelineRtId">Object identifier of pipeline</param>
    /// <returns></returns>
    Task<IEnumerable<RtConfiguration>> GetConfigurationsByPipelineAsync(string tenantId, OctoObjectId pipelineRtId);

    /// <summary>
    /// Gets a list of triggers of the given tenant
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <returns></returns>
    Task<IReadOnlyCollection<RtPipelineTrigger>> GetTriggersAsync(string tenantId);

    /// <summary>
    /// Gets a list of triggers and their pipelines of the given tenant
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <returns></returns>
    Task<IDictionary<RtPipelineTrigger, IList<RtPipeline>>> GetTriggersAndPipelinesAsync(string tenantId);

    /// <summary>
    /// Set the deployment state of a pipeline trigger
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="triggerRtId">Object id of trigger</param>
    /// <param name="deploymentState">State of trigger</param>
    /// <returns></returns>
    Task SetPipelineTriggerDeploymentStateAsync(string tenantId, OctoObjectId triggerRtId,
        RtDeploymentStateEnum deploymentState);

    /// <summary>
    /// Set the deployment state of a pipeline
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="pipelineRtEntityId">Object id of the pipeline</param>
    /// <param name="deploymentState">State of the pipeline</param>
    /// <param name="stateMessage">Optional status message</param>
    /// <returns></returns>
    Task SetPipelineDeploymentStateAsync(string tenantId, RtEntityId pipelineRtEntityId,
        RtDeploymentStateEnum deploymentState, string? stateMessage);

    /// <summary>
    /// Set the pipeline definition YAML of a pipeline
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="pipelineRtEntityId">Object id of the pipeline</param>
    /// <param name="pipelineDefinition">Pipeline definition YAML</param>
    /// <param name="executionClass">
    ///     Optional <c>PipelineExecutionClass</c> key resolved from the definition's trigger node
    ///     (AB#4924), written in the SAME update so the class and the YAML it describes can never
    ///     disagree. Null leaves the persisted value untouched.
    /// </param>
    /// <returns></returns>
    Task SetPipelineDefinitionAsync(string tenantId, RtEntityId pipelineRtEntityId,
        string pipelineDefinition, int? executionClass = null);

    /// <summary>
    ///     Writes only the <c>PipelineExecutionClass</c> of a pipeline (AB#4924), for the redeploy
    ///     path where the definition is unchanged but the resolved class may not be. Best-effort:
    ///     never throws, because a display/scheduling hint must not fail a deploy.
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="pipelineRtEntityId">Object id of the pipeline</param>
    /// <param name="executionClass">The resolved <c>PipelineExecutionClass</c> key</param>
    Task SetPipelineExecutionClassAsync(string tenantId, RtEntityId pipelineRtEntityId, int executionClass);

    /// <summary>
    /// Sets the debugging enabled state of a pipeline
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="pipelineRtEntityId">Object id of the pipeline</param>
    /// <param name="isDebuggingEnabled">Whether debugging is enabled</param>
    Task SetPipelineDebuggingEnabledAsync(string tenantId, RtEntityId pipelineRtEntityId,
        bool isDebuggingEnabled);

    /// <summary>
    /// Synchronizes the SendsDataTo associations for a pipeline based on ToPipelineDataEvent nodes
    /// in its definition. Adds new associations and removes stale ones.
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="pipelineRtEntityId">Object id of the pipeline</param>
    /// <param name="pipelineDefinition">Pipeline definition YAML</param>
    Task SyncPipelineDataConnectionsAsync(string tenantId, RtEntityId pipelineRtEntityId,
        string pipelineDefinition);

    /// <summary>
    /// Set the configuration state of an adapter
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="adapterRtEntityId">Object identifier of communication adapter</param>
    /// <param name="configurationState">Configuration state</param>
    /// <param name="stateMessage">An optional status message</param>
    /// <returns></returns>
    Task SetAdapterConfigurationStateAsync(string tenantId, RtEntityId adapterRtEntityId, RtConfigurationStateEnum configurationState, string? stateMessage);

    #region Pipeline Execution

    /// <summary>
    /// Creates a new pipeline execution record
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="execution">The execution entity to create</param>
    /// <param name="pipelineRtEntityId">Pipeline being executed</param>
    /// <param name="adapterRtEntityId">Adapter executing the pipeline</param>
    Task CreatePipelineExecutionAsync(string tenantId, RtPipelineExecution execution,
        RtEntityId pipelineRtEntityId, RtEntityId adapterRtEntityId);

    /// <summary>
    /// Updates an existing pipeline execution record
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="executionId">Execution ID (GUID as string)</param>
    /// <param name="status">New status</param>
    /// <param name="completedAt">Completion timestamp</param>
    /// <param name="durationMs">Duration in milliseconds</param>
    /// <param name="errorMessage">Error message if failed</param>
    /// <param name="outputData">Optional output data (JSON) from pipeline result</param>
    Task UpdatePipelineExecutionAsync(string tenantId, string executionId,
        RtPipelineExecutionStatusEnum status, DateTime? completedAt, int? durationMs, string? errorMessage,
        string? outputData = null);

    /// <summary>
    /// Gets a pipeline execution by its execution ID
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="executionId">Execution ID (GUID as string)</param>
    /// <returns>The execution or null if not found</returns>
    Task<RtPipelineExecution?> GetPipelineExecutionAsync(string tenantId, string executionId);

    /// <summary>
    /// Gets pipeline executions for a specific pipeline
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="pipelineRtEntityId">Pipeline identifier</param>
    /// <param name="from">Optional start date filter</param>
    /// <param name="to">Optional end date filter</param>
    /// <param name="limit">Optional result limit</param>
    /// <returns>List of executions</returns>
    Task<IReadOnlyList<RtPipelineExecution>> GetPipelineExecutionsAsync(string tenantId,
        RtEntityId pipelineRtEntityId, DateTime? from, DateTime? to, int? limit);

    /// <summary>
    /// Gets a page of pipeline executions for a specific pipeline.
    /// Uses skip/take pagination which triggers the optimized MongoDB query path
    /// with $limit inside $lookup, avoiding the 16MB BSON document size limit.
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="pipelineRtEntityId">Pipeline identifier</param>
    /// <param name="from">Optional start date filter</param>
    /// <param name="to">Optional end date filter</param>
    /// <param name="skip">Number of results to skip</param>
    /// <param name="take">Number of results to take</param>
    /// <returns>List of executions for the requested page</returns>
    Task<IReadOnlyList<RtPipelineExecution>> GetPipelineExecutionsAsync(string tenantId,
        RtEntityId pipelineRtEntityId, DateTime? from, DateTime? to, int skip, int take);

    /// <summary>
    /// Gets all running executions for a specific adapter
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="adapterRtEntityId">Adapter identifier</param>
    /// <returns>List of running executions</returns>
    Task<IReadOnlyList<RtPipelineExecution>> GetRunningExecutionsForAdapterAsync(string tenantId,
        RtEntityId adapterRtEntityId);

    /// <summary>
    /// Gets execution IDs that are in Interrupted state for an adapter
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="adapterRtEntityId">Adapter identifier</param>
    /// <returns>List of interrupted execution IDs</returns>
    Task<IReadOnlyList<string>> GetInterruptedExecutionIdsAsync(string tenantId, RtEntityId adapterRtEntityId);

    /// <summary>
    /// Gets terminal (non-Running) executions of a pipeline that started before the given cutoff,
    /// oldest first. Used by the statistics fold (AB#4370) to drain executions in batches.
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="pipelineRtEntityId">Pipeline identifier</param>
    /// <param name="olderThan">Only executions started before this time</param>
    /// <param name="take">Maximum batch size</param>
    /// <returns>Batch of terminal executions, oldest first</returns>
    Task<IReadOnlyList<RtPipelineExecution>> GetTerminalExecutionsOlderThanAsync(string tenantId,
        RtEntityId pipelineRtEntityId, DateTime olderThan, int take);

    /// <summary>
    /// Physically erases the given execution entities (and their association edges).
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="executionRtEntityIds">Executions to erase</param>
    /// <returns>Number of deleted executions</returns>
    Task<int> DeleteExecutionsAsync(string tenantId, IReadOnlyList<RtEntityId> executionRtEntityIds);

    /// <summary>
    /// Deletes executions older than the specified date
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="olderThan">Delete executions older than this date</param>
    /// <returns>Number of deleted executions</returns>
    Task<int> DeleteOldExecutionsAsync(string tenantId, DateTime olderThan);

    /// <summary>
    /// Finds running executions older than the specified date and marks them as failed
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="olderThan">Timeout threshold - executions started before this date are considered stale</param>
    /// <returns>Number of timed out executions</returns>
    Task<int> TimeoutStaleExecutionsAsync(string tenantId, DateTime olderThan);

    /// <summary>
    /// Connection-aware reaper. Fails executions that are stuck in a non-terminal state past the
    /// grace cutoff: all <c>Interrupted</c> executions, plus <c>Running</c> executions whose owning
    /// adapter is not <c>Online</c>. Running executions on a live (Online) adapter are never failed,
    /// so legitimate long-running pipelines are unaffected.
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="graceCutoff">Executions started before this instant are eligible</param>
    /// <returns>Number of executions failed</returns>
    Task<int> FailStuckExecutionsAsync(string tenantId, DateTime graceCutoff);

    /// <summary>
    /// Fails all non-terminal (<c>Running</c> / <c>Interrupted</c>) executions for the given adapter
    /// that started before <paramref name="beforeUtc"/>. Called when a freshly (re)started adapter
    /// process registers: such a process cannot own any earlier execution, so those records are
    /// orphans from the previous process and are transitioned to <c>Failed</c>.
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="adapterRtEntityId">The (re)started adapter</param>
    /// <param name="beforeUtc">The adapter process start time; executions started earlier are orphans</param>
    /// <returns>Number of executions failed</returns>
    Task<int> FailOrphanedExecutionsForAdapterAsync(string tenantId, RtEntityId adapterRtEntityId, DateTime beforeUtc);

    #endregion

    #region Adapter pool queue (AB#4924 increment 7)

    /// <summary>
    /// Creates a pipeline execution in <c>Queued</c> state with <c>QueuedAt</c> stamped, instead of
    /// sending the work to the execute queue (AB#4924 §9.1). This is the only writer of
    /// <c>Queued</c>: a manual adapter has no queue and executes immediately, exactly as today.
    /// </summary>
    /// <remarks>
    /// <c>StartedAt</c> is deliberately left unset. Stamping it here would make it lie for the whole
    /// queue wait, and both AB#4280 reapers use it as their age key — a work item that waited
    /// twenty minutes in a healthy queue would be reaped as stuck (concept §8, Q5).
    /// </remarks>
    /// <param name="tenantId">The BORROWING tenant — the execution is its entity</param>
    /// <param name="execution">The execution entity to create; Status and QueuedAt are set here</param>
    /// <param name="pipelineRtEntityId">Pipeline that will be executed</param>
    /// <param name="adapterRtEntityId">The borrower's own Leased adapter</param>
    /// <param name="queuedAtUtc">Enqueue timestamp</param>
    Task EnqueueExecutionAsync(string tenantId, RtPipelineExecution execution,
        RtEntityId pipelineRtEntityId, RtEntityId adapterRtEntityId, DateTime queuedAtUtc);

    /// <summary>
    /// Reads the work items of one borrowing adapter that are waiting for a lease, oldest first
    /// (AB#4924 §9.3). Ordered by <c>QueuedAt</c> — the index added on that attribute in 4.0.0, not
    /// the <c>StartedAt</c> index, which a queued execution does not populate at all.
    /// </summary>
    /// <param name="tenantId">Borrowing tenant</param>
    /// <param name="adapterRtEntityId">The borrower's Leased adapter</param>
    /// <param name="take">Maximum entries to return</param>
    Task<IReadOnlyList<QueuedExecution>> GetQueuedExecutionsForAdapterAsync(string tenantId,
        RtEntityId adapterRtEntityId, int take);

    /// <summary>
    /// Position of one work item inside its own tenant's queue, 1-based; <c>0</c> when the execution
    /// is not (or no longer) queued.
    /// </summary>
    /// <remarks>
    /// 🔴 Per tenant, never global. Round-robin has no global rank to report, and inventing one
    /// would be a number the scheduler does not act on (concept §5, "Fairness").
    /// </remarks>
    Task<int> GetQueuedExecutionPositionAsync(string tenantId, string executionId);

    /// <summary>
    /// Takes one work item out of the queue and marks it running under a lease:
    /// <c>Queued → Running</c>, <c>LeaseGrantedAt</c> / <c>StartedAt</c> stamped,
    /// <c>LeaseWaitMs = LeaseGrantedAt - QueuedAt</c>, and the lease provenance recorded
    /// (AB#4924 §9.1).
    /// </summary>
    /// <remarks>
    /// Returns <c>false</c> when the claim did not apply because someone else already took it —
    /// another controller pod, or a cancellation that landed first. Callers must undo whatever they
    /// reserved in anticipation (a pool member, above all) rather than treat the grant as done.
    /// </remarks>
    /// <returns><c>true</c> when this caller now owns the work item</returns>
    Task<bool> TryClaimQueuedExecutionAsync(string tenantId, string executionId, LeaseClaim claim);

    /// <summary>
    /// Cancels a work item that is still waiting for a lease: <c>Queued → Cancelled</c>
    /// (AB#4924 §9.5). Returns <c>false</c> when the execution no longer exists or has left the
    /// queue — cancelling an execution that already HOLDS a lease is a different operation and
    /// follows the existing cancellation path.
    /// </summary>
    Task<bool> TryCancelQueuedExecutionAsync(string tenantId, string executionId, string? reason);

    /// <summary>
    /// Stamps <c>LeaseReleasedAt</c> on a leased execution (AB#4924 §9.1).
    /// </summary>
    /// <remarks>
    /// 🔴 Called on every release path including TTL expiry and crash, because the span
    /// <c>LeaseGrantedAt..LeaseReleasedAt</c> is a billing input (concept §4b) and a span stamped
    /// only on the happy path silently under-bills exactly the failures a borrower did pay for.
    /// Never changes the status — the status transition belongs to whoever caused the release.
    /// </remarks>
    Task StampLeaseReleasedAsync(string tenantId, string executionId, DateTime releasedAtUtc);

    /// <summary>
    /// Marks a leased attempt <c>Interrupted</c> and stamps <c>LeaseReleasedAt</c> (AB#4924 §9.3,
    /// concept §6). Used on TTL expiry and on a member that vanished mid-lease.
    /// </summary>
    /// <returns>
    /// The pipeline and adapter the attempt belonged to, so the caller can enqueue a fresh attempt,
    /// or <c>null</c> when the execution could not be read or had already reached a terminal state.
    /// </returns>
    Task<InterruptedLeasedExecution?> TryInterruptLeasedExecutionAsync(string tenantId, string executionId,
        DateTime releasedAtUtc, string reason);

    /// <summary>
    /// Reads one execution as a queue entry — pipeline and execution class attached — whatever its
    /// status. Used by the pool queue surface to describe an entry that is already leased and
    /// therefore no longer part of a queue read.
    /// </summary>
    Task<QueuedExecution?> GetExecutionQueueEntryAsync(string tenantId, string executionId);

    #endregion

    #region Pipeline Statistics

    /// <summary>
    /// Gets or creates statistics for a specific pipeline
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="pipelineRtEntityId">Pipeline identifier</param>
    /// <returns>Statistics entity or null if not found</returns>
    Task<RtPipelineStatistics?> GetPipelineStatisticsAsync(string tenantId, RtEntityId pipelineRtEntityId);

    /// <summary>
    /// Creates or updates pipeline statistics
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="statistics">Statistics to upsert</param>
    /// <param name="pipelineRtEntityId">Pipeline identifier</param>
    Task UpsertPipelineStatisticsAsync(string tenantId, RtPipelineStatistics statistics,
        RtEntityId pipelineRtEntityId);

    /// <summary>
    /// Gets aggregated execution statistics for a pipeline within a time range
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="pipelineRtEntityId">Pipeline identifier</param>
    /// <param name="from">Start of time range</param>
    /// <param name="to">End of time range</param>
    /// <returns>Aggregated statistics</returns>
    Task<ExecutionAggregateResult> GetExecutionAggregateAsync(string tenantId,
        RtEntityId pipelineRtEntityId, DateTime from, DateTime to);

    #endregion

    /// <summary>
    /// Bulk updates pipeline executions (queries all by executionId IN filter, then applies updates in one transaction)
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="updates">List of updates to apply</param>
    /// <returns>Number of successfully updated executions</returns>
    Task<int> BulkUpdatePipelineExecutionsAsync(string tenantId,
        IReadOnlyList<PipelineExecutionUpdate> updates);

    #region Bulk Operations (for offline sync)

    /// <summary>
    /// Bulk inserts pipeline executions
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="executions">Executions to insert</param>
    /// <param name="pipelineRtEntityId">Pipeline identifier</param>
    /// <param name="adapterRtEntityId">Adapter identifier</param>
    Task BulkInsertPipelineExecutionsAsync(string tenantId, IEnumerable<RtPipelineExecution> executions,
        RtEntityId pipelineRtEntityId, RtEntityId adapterRtEntityId);

    /// <summary>
    /// Gets existing execution IDs from a list (for deduplication)
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="executionIds">List of execution IDs to check</param>
    /// <returns>Set of existing execution IDs</returns>
    Task<ISet<string>> GetExistingExecutionIdsAsync(string tenantId, IEnumerable<string> executionIds);

    /// <summary>
    /// Updates the last synced sequence number for an adapter
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="adapterRtEntityId">Adapter identifier</param>
    /// <param name="sequenceNumber">New sequence number</param>
    Task UpdateAdapterSyncSequenceNumberAsync(string tenantId, RtEntityId adapterRtEntityId, int sequenceNumber);

    /// <summary>
    /// Gets the last synced sequence number for an adapter
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="adapterRtEntityId">Adapter identifier</param>
    /// <returns>Last synced sequence number</returns>
    Task<int> GetAdapterSyncSequenceNumberAsync(string tenantId, RtEntityId adapterRtEntityId);

    #endregion

    #region Pipeline Queries for Statistics

    /// <summary>
    /// Gets all pipelines for a tenant
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <returns>List of all pipelines</returns>
    Task<IReadOnlyCollection<RtPipeline>> GetAllPipelinesAsync(string tenantId);

    #endregion

    #region Rights analysis (AB#5113) — System.Identity reads

    /// <summary>
    /// AB#5113: the pipelines whose generic <c>Uses</c> edge points at the given
    /// <c>ServiceAccountConfiguration</c> — the reverse of
    /// <see cref="GetConfigurationsByPipelineAsync" />, restricted to pipeline origins. These are
    /// the per-pipeline override candidates for that configuration (whether an edge is the
    /// <em>effective</em> override is the resolver's call — a pipeline may link several
    /// configurations). Returns an empty list when nothing links the configuration.
    /// </summary>
    Task<IReadOnlyCollection<RtPipeline>> GetPipelinesUsingServiceAccountAsync(string tenantId,
        OctoObjectId serviceAccountRtId);

    /// <summary>
    /// AB#5113: all <c>System.Identity/DataPolicy</c> entities of the tenant. Read untyped — the
    /// controller references no generated <c>System.Identity</c> model (see
    /// <see cref="SystemIdentityCkIds" />); the analysis service interprets the attributes
    /// (<c>TargetCkTypeIds</c>, <c>Scope</c>, <c>EnforcementMode</c>). The full list is small by
    /// construction (a handful of policies per tenant) and the analysis needs every policy to
    /// decide which touched types are protected.
    /// </summary>
    Task<IReadOnlyCollection<RtEntity>> GetDataPoliciesAsync(string tenantId);

    /// <summary>
    /// AB#5113: the <c>System.Identity/DataPermission</c> entities each given policy binds through
    /// its <c>PolicyPermission</c> edge, keyed by policy rtId. Policies without a permission edge
    /// are absent from the result.
    /// </summary>
    Task<IReadOnlyDictionary<OctoObjectId, IReadOnlyList<RtEntity>>> GetDataPermissionsForPoliciesAsync(
        string tenantId, IReadOnlyCollection<OctoObjectId> policyRtIds);

    /// <summary>
    /// AB#5113: the <c>System.Identity/Role</c> entities granted each given data permission
    /// (inbound over the <c>GrantsPermission</c> edge — the edge lives on the role), keyed by
    /// permission rtId. Permissions no role holds are absent from the result.
    /// </summary>
    Task<IReadOnlyDictionary<OctoObjectId, IReadOnlyList<RtEntity>>> GetGrantingRolesForDataPermissionsAsync(
        string tenantId, IReadOnlyCollection<OctoObjectId> permissionRtIds);

    #endregion
}