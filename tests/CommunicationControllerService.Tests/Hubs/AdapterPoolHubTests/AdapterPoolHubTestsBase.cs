using Meshmakers.Octo.Backend.CommunicationControllerServices.Hubs;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Options;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Hubs.AdapterPoolHubTests;

/// <summary>
///     Shared arrangement for the <c>/adapterPoolHub</c> tests (AB#4924 increment 6).
/// </summary>
/// <remarks>
///     The connection manager is the <b>real</b> one rather than a substitute: it owns the
///     claim/release transition that the isolation invariant rests on, so a hub test that asserted
///     against a mocked registry would prove only that the hub called a method.
/// </remarks>
internal abstract class AdapterPoolHubTestsBase : IDisposable
{
    protected const string ConnectionId = "conn-pool-member-1";
    protected const string LenderTenantId = "lender";
    protected const string BorrowerTenantId = "borrower";
    protected const string PoolRtId = "6ad562f3ff7c40ff80275b84";
    protected const string MemberId = "octo-pool-0";

    protected readonly IAdapterPoolConnectionManager ConnectionManager = new AdapterPoolConnectionManager();
    protected readonly ICommunicationEventService EventService = Substitute.For<ICommunicationEventService>();
    protected readonly ILeaseService LeaseService = Substitute.For<ILeaseService>();
    protected readonly IShutdownState ShutdownState = Substitute.For<IShutdownState>();

    protected readonly AdapterPoolHubAuthorizationOptions AuthorizationOptions = new();

    protected readonly AdapterPoolHub Hub;
    protected readonly Dictionary<object, object?> ContextItems = new();

    protected AdapterPoolHubTestsBase()
    {
        Hub = new AdapterPoolHub(ConnectionManager, EventService, LeaseService,
            new OptionsWrapper<AdapterPoolHubAuthorizationOptions>(AuthorizationOptions), ShutdownState);

        var context = Substitute.For<HubCallerContext>();
        context.ConnectionId.Returns(ConnectionId);
        context.Items.Returns(ContextItems);
        Hub.Context = context;
    }

    /// <summary>
    ///     Puts the connection's token tenant where the gate would have put it. The gate itself is
    ///     covered by <c>AdapterPoolHubAuthorizationFilterTests</c>; these tests are about what the
    ///     hub does with the answer.
    /// </summary>
    protected void ArrangeConnectionTenant(string? tenantId)
    {
        if (tenantId is null)
        {
            ContextItems.Remove(AdapterPoolHubAuthorizationFilter.ConnectionTenantIdItemKey);
            return;
        }

        ContextItems[AdapterPoolHubAuthorizationFilter.ConnectionTenantIdItemKey] = tenantId;
    }

    public void Dispose()
    {
        Hub.Dispose();
        GC.SuppressFinalize(this);
    }
}
