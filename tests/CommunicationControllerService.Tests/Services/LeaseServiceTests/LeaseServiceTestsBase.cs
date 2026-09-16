using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Hubs;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using Microsoft.AspNetCore.SignalR;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.LeaseServiceTests;

/// <summary>
///     Shared arrangement for <c>LeaseService</c> (AB#4924 increment 6).
/// </summary>
/// <remarks>
///     The connection manager is the real one — the claim/release transition is the thing under test
///     as much as the service is. Everything that reads a tenant database is substituted: what
///     matters here is which checks run, in which order, and what the lease ends up carrying.
/// </remarks>
internal abstract class LeaseServiceTestsBase
{
    protected const string LenderTenantId = "lender";
    protected const string BorrowerTenantId = "borrower";
    protected const string ConnectionId = "conn-pool-member-1";
    protected const string MemberId = "octo-pool-0";
    protected const string ClientSecret = "sJ8k2p-QmZ4x7vNb1LcT0aRwEyUiOpAsDfGhJkLzXcVbNm";

    protected const string PipelineInput = "{\"invoiceNumber\":\"BORROWER-PRIVATE-4711\"}";
    protected const string ConfigurationSecret = "cfgSecret-9F2a7Lq0ZxBv3TnE8Rd1Yh6Ks4Mw5Pu2";

    /// <summary>
    ///     AB#4924 — the borrowing tenant's database, and the credential that opens it. The password
    ///     deliberately shares no prefix with <see cref="ClientSecret" />: an assertion that passed
    ///     only because the client secret was redacted must not be able to pass for this one by
    ///     accident.
    /// </summary>
    protected const string BorrowerDatabaseName = "borrowerdb";

    protected const string BorrowerDatabaseUser = "octo-system-ds-user-borrowerdb";
    protected const string BorrowerDatabasePassword = "dbPwd-Qv7Xr2Mn8Kt4Ws0Yh3Bd6Lp9Cf1Zg5Ja";

    /// <summary>The LENDER's database credential — what a member must never be handed for a borrower.</summary>
    protected const string LenderDatabaseUser = "octo-system-ds-user-lenderdb";

    /// <summary>
    ///     A fresh pool per test instance, not a shared constant. The increment 9 leasing metrics are
    ///     process-wide statics tagged by pool rtId and TUnit runs these tests concurrently, so a
    ///     shared id would let one test read another's measurements.
    /// </summary>
    protected readonly OctoObjectId PoolRtId = OctoObjectId.GenerateNewId();

    protected static readonly OctoObjectId PipelineRtId = new("6ad562f3ff7c40ff80275b85");

    protected readonly IAdapterPoolConnectionManager ConnectionManager = new AdapterPoolConnectionManager();
    protected readonly ICommunicationRepository CommunicationRepository =
        Substitute.For<ICommunicationRepository>();
    protected readonly ICommunicationEventService EventService = Substitute.For<ICommunicationEventService>();
    protected readonly IWorkloadEncryptionService EncryptionService =
        Substitute.For<IWorkloadEncryptionService>();
    protected readonly IHubContext<AdapterPoolHub> HubContext = Substitute.For<IHubContext<AdapterPoolHub>>();
    protected readonly ITenantLendingScopeResolver LendingScopeResolver =
        Substitute.For<ITenantLendingScopeResolver>();
    protected readonly IPipelineServiceAccountResolver ServiceAccountResolver =
        Substitute.For<IPipelineServiceAccountResolver>();

    /// <summary>
    ///     AB#4924 — the borrower's <b>data</b> credential. Substituted for the same reason the
    ///     service-account resolver is: what matters here is that the lease carries what the resolver
    ///     produced for the BORROWER, and that a lease is refused rather than granted blank when it
    ///     produced nothing.
    /// </summary>
    protected readonly ITenantDatabaseCredentialResolver DatabaseCredentialResolver =
        Substitute.For<ITenantDatabaseCredentialResolver>();

    /// <summary>
    ///     AB#4924 §9.9 / D4 — the lease carries the work, and the projection that produces it is the
    ///     controller's, not the member's.
    /// </summary>
    protected readonly IAdapterService AdapterService = Substitute.For<IAdapterService>();

    /// <summary>
    ///     AB#4924 §14 — the per-tenant leasing kill switch. Default <b>on</b> for both tenants, so
    ///     every test written before it existed still exercises what it meant to; the gate's own suite
    ///     turns it off explicitly, one tenant at a time.
    /// </summary>
    protected readonly ILifecycleConfigurationService LifecycleConfiguration =
        Substitute.For<ILifecycleConfigurationService>();

    protected readonly ISingleClientProxy MemberProxy = Substitute.For<ISingleClientProxy>();
    protected readonly ILeaseService LeaseService;

    protected RtAdapter Borrower = null!;

    /// <summary>
    ///     AB#4924 §9.6 — the scheduler's wake signal. A release makes a member available, and the
    ///     next grant must not have to wait for the tick.
    /// </summary>
    protected readonly ILeaseSchedulerWakeSignal WakeSignal = Substitute.For<ILeaseSchedulerWakeSignal>();

    protected LeaseServiceTestsBase()
    {
        var clients = Substitute.For<IHubClients>();
        clients.Client(Arg.Any<string>()).Returns(MemberProxy);
        HubContext.Clients.Returns(clients);

        // Decrypt is a pass-through without the `enc:v1:` sentinel, exactly as in production.
        EncryptionService.Decrypt(Arg.Any<string>()).Returns(call => call.Arg<string>());

        LifecycleConfiguration.IsLeasingEnabledAsync(Arg.Any<string>()).Returns(true);

        // Resolvable by default, for the BORROWER only: a test that forgot to arrange it would
        // otherwise be refused for a reason it never meant to exercise. The lender's tenant resolves
        // to the lender's own credential, so "the lease carries the borrower's" is a statement a test
        // can actually falsify.
        DatabaseCredentialResolver.TryResolveAsync(BorrowerTenantId, Arg.Any<CancellationToken>())
            .Returns(new TenantDatabaseCredential(BorrowerDatabaseName, BorrowerDatabaseUser,
                BorrowerDatabasePassword));
        DatabaseCredentialResolver.TryResolveAsync(LenderTenantId, Arg.Any<CancellationToken>())
            .Returns(new TenantDatabaseCredential("lenderdb", LenderDatabaseUser, "lenderPwd-not-the-borrowers"));

        LeaseService = new LeaseService(ConnectionManager, CommunicationRepository, EventService,
            EncryptionService, HubContext, LendingScopeResolver, ServiceAccountResolver,
            DatabaseCredentialResolver, AdapterService, LifecycleConfiguration, WakeSignal);
    }

    /// <summary>
    ///     The happy path: a borrower that declares this pool, a pool that lends here, a credential
    ///     that exists, and one idle member connected.
    /// </summary>
    protected void ArrangeGrantableLease(string clientSecret = ClientSecret)
    {
        ArrangeBorrower();
        ArrangeLendingPool(lends: true);
        ArrangeBorrowerCredential(clientSecret);
        ArrangeConnectedMember();
    }

    protected RtAdapter ArrangeBorrower(RtLifecycleModeEnum lifecycleMode = RtLifecycleModeEnum.Leased,
        string? lentFromTenantId = LenderTenantId, string? lentFromPoolRtId = null)
    {
        Borrower = RtEntityCreator.CreateAdapter();
        Borrower.Name = "borrowing-adapter";
        Borrower.LifecycleMode = lifecycleMode;
        Borrower.LentFromTenantId = lentFromTenantId;
        Borrower.LentFromPoolRtId = lentFromPoolRtId ?? PoolRtId.ToString();

        CommunicationRepository.GetWorkloadByRtIdAsync(BorrowerTenantId, Borrower.RtId).Returns(Borrower);
        return Borrower;
    }

    protected void ArrangeLendingPool(bool lends, int sharingMode = LendingScope.Descendants)
    {
        var scope = new LendingScope(sharingMode, null);
        CommunicationRepository.TryGetAdapterPoolLendingScopeAsync(LenderTenantId, PoolRtId.ToString())
            .Returns(scope);
        LendingScopeResolver
            .MayLendAsync(LenderTenantId, BorrowerTenantId, scope, Arg.Any<CancellationToken>())
            .Returns(lends);
    }

    /// <summary>The borrower's database credential cannot be resolved — no tenant record, or no
    ///     datasource configuration on this controller.</summary>
    protected void ArrangeUnresolvableBorrowerDatabaseCredential()
    {
        DatabaseCredentialResolver.TryResolveAsync(BorrowerTenantId, Arg.Any<CancellationToken>())
            .Returns((TenantDatabaseCredential?)null);
    }

    protected void ArrangeNoPool()
    {
        CommunicationRepository.TryGetAdapterPoolLendingScopeAsync(LenderTenantId, PoolRtId.ToString())
            .Returns((LendingScope?)null);
    }

    protected RtServiceAccountConfiguration ArrangeBorrowerCredential(string clientSecret = ClientSecret,
        string clientId = "octo-pipeline-sa-borrower")
    {
        var configuration = RtEntityCreator.CreateServiceAccountConfiguration("borrower-service-account");
        configuration.ClientId = clientId;
        configuration.ClientSecret = clientSecret;
        ServiceAccountResolver.GetAdapterDefaultAsync(BorrowerTenantId, Borrower.RtId).Returns(configuration);
        return configuration;
    }

    protected void ArrangeConnectedMember(string connectionId = ConnectionId, string memberId = MemberId)
    {
        ConnectionManager.RegisterMember(connectionId, memberId, LenderTenantId, PoolRtId.ToString());
    }

    protected LeaseRequest ARequest(string? executionId = null, TimeSpan? ttl = null)
    {
        return new LeaseRequest(BorrowerTenantId, Borrower.RtId, executionId, ttl);
    }

    /// <summary>
    ///     A request that names work — an execution AND the pipeline to run for it (AB#4924 §9.9 / D4).
    /// </summary>
    protected LeaseRequest AWorkRequest(string executionId = "exec-1", string? input = PipelineInput)
    {
        return new LeaseRequest(BorrowerTenantId, Borrower.RtId, executionId, null, PipelineRtId, input);
    }

    /// <summary>
    ///     The controller's projection of the pipeline the lease is to run. Carries a second secret of
    ///     its own on purpose: AB#5027 puts the pipeline service account into exactly these entries, so
    ///     the log-target assertion has something to be about beyond the lease's own credential.
    /// </summary>
    protected PipelineConfigurationDto ArrangeProjectablePipeline(string? configurationSecret = ConfigurationSecret)
    {
        var configuration = new PipelineConfigurationDto(
            new OctoObjectId("665f0000000000000000ee24"),
            new RtEntityId(SystemCommunicationCkIds.RtCkPipelineTypeId, PipelineRtId),
            false,
            "triggers:\n  - node: FromExecutePipelineCommand@1\n",
            [
                new ConfigurationDto(new OctoObjectId("665f0000000000000000ee25"),
                    SystemCommunicationCkIds.RtCkServiceAccountConfigurationTypeId, "adapter-service-account",
                    $"{{\"attributes\":{{\"clientSecret\":\"{configurationSecret}\"}}}}")
            ]);

        AdapterService.GetLeasedPipelineConfigurationAsync(BorrowerTenantId, Arg.Any<RtEntityId>(), PipelineRtId)
            .Returns(configuration);
        return configuration;
    }

    /// <summary>The projection fails — a pipeline that is gone, disabled, or on another adapter.</summary>
    protected void ArrangeUnprojectablePipeline()
    {
        AdapterService.GetLeasedPipelineConfigurationAsync(BorrowerTenantId, Arg.Any<RtEntityId>(), PipelineRtId)
            .Returns((PipelineConfigurationDto?)null);
    }

    /// <summary>
    ///     The <see cref="LeaseDto" /> actually pushed down the member's connection.
    /// </summary>
    protected LeaseDto CapturePushedLease()
    {
        var call = MemberProxy.ReceivedCalls()
            .Single(c => c.GetMethodInfo().Name == nameof(IClientProxy.SendCoreAsync));
        var arguments = (object?[])call.GetArguments()[1]!;
        return (LeaseDto)arguments[0]!;
    }
}
