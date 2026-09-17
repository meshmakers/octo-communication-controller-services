using Meshmakers.Octo.Backend.CommunicationControllerServices.Options;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Contracts.Hubs;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using NLog;

// ReSharper disable UnusedMember.Global

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Hubs;

/// <summary>
///     Management channel of adapter-pool members (AB#4924, concept §4).
/// </summary>
/// <remarks>
///     <para>
///         🔴 <b>Not <see cref="AdapterHub" />, and not a widening of it.</b>
///         <c>/{tenantId:tenantId}/adapterHub</c> is tenant-addressed by construction and
///         <c>AdapterHubAuthorizationFilter</c> (AB#5063) exists to bind a connection to its route
///         tenant. A pool member belongs to no tenant, so it cannot use that route — and relaxing the
///         filter so that it could would remove the only tenant check the adapter data plane has
///         (implementation plan §13.6).
///     </para>
///     <para>
///         The member's connection is authorized against the <b>lending</b> tenant and proves
///         membership of a pool that tenant owns. Every authority it exercises inside a
///         <i>borrowing</i> tenant arrives on the lease and dies with it.
///     </para>
/// </remarks>
internal class AdapterPoolHub : Hub, IAdapterPoolHub
{
    /// <summary>
    ///     Heartbeat cadence handed to a member at registration. Comfortably shorter than the
    ///     shortest sensible lease TTL, so a wedged member is visible well before its lease expires.
    /// </summary>
    public const int HeartbeatIntervalSeconds = 30;

    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private readonly IAdapterPoolConnectionManager _connectionManager;
    private readonly ICommunicationEventService _eventService;
    private readonly ILeaseService _leaseService;
    private readonly IOptions<AdapterPoolHubAuthorizationOptions> _authorizationOptions;
    private readonly IShutdownState _shutdownState;

    /// <summary>
    ///     Constructor.
    /// </summary>
    /// <param name="connectionManager">Registry of the pool members connected to this instance.</param>
    /// <param name="eventService">Service for storing system events.</param>
    /// <param name="leaseService">Grants and releases leases.</param>
    /// <param name="authorizationOptions">Staged enforcement mode of the pool hub gate.</param>
    /// <param name="shutdownState">Reports whether the controller is mid-shutdown.</param>
    public AdapterPoolHub(IAdapterPoolConnectionManager connectionManager,
        ICommunicationEventService eventService,
        ILeaseService leaseService,
        IOptions<AdapterPoolHubAuthorizationOptions> authorizationOptions,
        IShutdownState shutdownState)
    {
        _connectionManager = connectionManager;
        _eventService = eventService;
        _leaseService = leaseService;
        _authorizationOptions = authorizationOptions;
        _shutdownState = shutdownState;
    }

    /// <inheritdoc />
    public override Task OnConnectedAsync()
    {
        Logger.Info("Pool member connected with connection id '{ConnectionId}'", Context.ConnectionId);
        return base.OnConnectedAsync();
    }

    /// <inheritdoc />
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var connectionId = Context.ConnectionId;
        Logger.Info("Pool member disconnected with connection id '{ConnectionId}'", connectionId);

        var member = _connectionManager.RemoveMember(connectionId);

        // Rolling-upgrade race guard, the same one AdapterHub and OperatorHub make: during this
        // pod's own shutdown the member has already reconnected (or is about to) to a surviving
        // controller pod. Writing an interruption from here would report a failure for a work item
        // that is about to be re-leased by the pod that now owns the connection. Registration state
        // is still dropped locally so a late hub call does not see a stale member.
        if (member is not null && !_shutdownState.IsShuttingDown)
        {
            await _leaseService.HandleMemberDisconnectedAsync(member);
        }
        else if (member is not null)
        {
            Logger.Info(
                "App is stopping; skipping the interruption report for pool member '{MemberId}'. " +
                "A surviving pod owns the member's next registration.",
                member.MemberId);
        }

        await base.OnDisconnectedAsync(exception);
    }

    /// <inheritdoc />
    public async Task<PoolMemberRegistrationResultDto> RegisterPoolMemberAsync(
        PoolMemberRegistrationDto registration)
    {
        var connectionId = Context.ConnectionId;

        if (_shutdownState.IsShuttingDown)
        {
            // 🔴 Refuse rather than accept-and-drop. A member registered on a pod that is going away
            // would be handed a lease that dies with the pod, and the borrower's work would be
            // interrupted for no reason other than a rollout. Refusing sends it round the reconnect
            // loop to a surviving pod, which is exactly where it should be.
            Logger.Info(
                "Refusing a pool-member registration on connection '{ConnectionId}': this controller is shutting down",
                connectionId);
            return new PoolMemberRegistrationResultDto
            {
                Accepted = false,
                MemberId = registration.MemberId,
                HeartbeatIntervalSeconds = HeartbeatIntervalSeconds,
                StatusMessage = "The controller instance is shutting down; reconnect and register again."
            };
        }

        if (string.IsNullOrWhiteSpace(registration.AdapterPoolTenantId) ||
            !OctoObjectId.TryParse(registration.AdapterPoolRtId, out _))
        {
            // A typed refusal naming the offending field, for the same reason
            // OperatorHub.RegisterDeploymentSiteAsync validates its rtId up front: the member cannot act on
            // "'' is not a valid 24 digit hex string" and would simply retry the same broken
            // configuration forever.
            Logger.Warn(
                "Rejecting a pool-member registration on connection '{ConnectionId}': pool tenant " +
                "'{AdapterPoolTenantId}' / pool rtId '{AdapterPoolRtId}' is not a usable pair",
                connectionId, registration.AdapterPoolTenantId, registration.AdapterPoolRtId);
            throw new HubException(
                $"Invalid pool member registration: AdapterPoolTenantId '{registration.AdapterPoolTenantId}' and AdapterPoolRtId " +
                $"'{registration.AdapterPoolRtId}' must name a tenant and a 24-character hex ObjectId.");
        }

        var refusal = CheckConnectionTenantBinding(registration.AdapterPoolTenantId);
        if (refusal != null)
        {
            if (_authorizationOptions.Value.Mode == AdapterPoolHubAuthorizationMode.Enforce)
            {
                Logger.Warn(
                    "Refusing a pool-member registration on connection '{ConnectionId}': {Reason}",
                    connectionId, refusal);
                await _eventService.StoreErrorEventAsync(registration.AdapterPoolTenantId,
                    $"Refused a pool-member registration for pool {registration.AdapterPoolRtId}: {refusal}");
                throw new HubException($"Pool member registration refused: {refusal}.");
            }

            // LogOnly — same contract as the connection gate and the other two hubs: outcomes
            // unchanged, but every registration an enforcing run would refuse is on the record.
            Logger.Warn(
                "Pool-member registration on connection '{ConnectionId}' {Reason} and would be refused when " +
                "AdapterPoolHubAuthorization:Mode is Enforce",
                connectionId, refusal);
        }

        // A member that proposed no id still has to be identifiable: the connection id is stable for
        // the life of the registration, which is the span every LeasedOnMemberId written under it
        // refers to.
        var memberId = string.IsNullOrWhiteSpace(registration.MemberId)
            ? connectionId
            : registration.MemberId;

        // AB#4924: the descriptors travel with the registration and are stored per pool, because a
        // BORROWER's DeployPipeline has to ask "which nodes can this pool run" — its own Leased
        // adapter has no process, and therefore no descriptors, of its own.
        _connectionManager.RegisterMember(connectionId, memberId, registration.AdapterPoolTenantId,
            registration.AdapterPoolRtId, registration.NodeDescriptors, registration.PipelineSchemaJson);

        return new PoolMemberRegistrationResultDto
        {
            Accepted = true,
            MemberId = memberId,
            HeartbeatIntervalSeconds = HeartbeatIntervalSeconds
        };
    }

    /// <inheritdoc />
    public async Task ReleaseLeaseAsync(LeaseResultDto result)
    {
        if (string.IsNullOrWhiteSpace(result.LeaseId))
        {
            Logger.Warn("Ignoring a lease release with no lease id from connection '{ConnectionId}'",
                Context.ConnectionId);
            return;
        }

        await _leaseService.ReleaseLeaseAsync(Context.ConnectionId, result);
    }

    /// <inheritdoc />
    public Task HeartbeatAsync(PoolMemberHeartbeatDto heartbeat)
    {
        _connectionManager.Heartbeat(Context.ConnectionId,
            heartbeat.SampledAtUtc == default ? DateTime.UtcNow : heartbeat.SampledAtUtc);
        return Task.CompletedTask;
    }

    /// <summary>
    ///     Whether the declared pool tenant is the tenant this connection's token was issued for, or
    ///     the reason it is not.
    /// </summary>
    /// <remarks>
    ///     The second half of the binding the connection gate starts — see
    ///     <see cref="AdapterPoolHubAuthorizationFilter" />. It cannot live in the filter: the filter
    ///     runs at connect, before any hub method, and no pool has been declared yet at that point.
    /// </remarks>
    private string? CheckConnectionTenantBinding(string declaredAdapterPoolTenantId)
    {
        var connectionTenantId = AdapterPoolHubAuthorizationFilter.GetConnectionTenantId(Context);
        if (string.IsNullOrEmpty(connectionTenantId))
        {
            return "presents no tenant-bound token, so its claim to belong to a pool of tenant " +
                   $"'{declaredAdapterPoolTenantId}' cannot be checked";
        }

        if (!string.Equals(connectionTenantId, declaredAdapterPoolTenantId, StringComparison.OrdinalIgnoreCase))
        {
            // 🔴 No parent/ancestor allowance, deliberately — the same stance AB#5063 takes on the
            // adapter hub. A pool belongs to the tenant that owns it, and a credential of some other
            // tenant registering members into it would let that tenant receive leases carrying a
            // third tenant's service-account secret.
            return $"presents a token of tenant '{connectionTenantId}' but claims to belong to a pool of " +
                   $"tenant '{declaredAdapterPoolTenantId}'";
        }

        return null;
    }
}
