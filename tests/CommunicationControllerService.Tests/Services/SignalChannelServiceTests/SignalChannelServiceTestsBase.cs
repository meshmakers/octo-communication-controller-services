using Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Adapters;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Models;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Options;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.SignalChannelServiceTests;

/// <summary>
/// Shared arrangement for the AB#5143 Signal channel suites: mocked repository, bridge client,
/// tenant cache and event trail. Defaults: only <see cref="TenantId"/> is enabled, no tenant
/// stores a channel, and the instance defines <see cref="DefaultApiUrl"/> as the bridge default.
/// </summary>
internal abstract class SignalChannelServiceTestsBase
{
    protected const string TenantId = "tenantId";
    protected const string OtherTenantId = "otherTenantId";
    protected const string Number = "+436641234567";
    protected const string OtherNumber = "+436649999999";
    protected const string ApiUrl = "http://signal-bridge.example:8080";
    protected const string DefaultApiUrl = "http://signal-cli-rest-api.signal-bridge.svc.cluster.local:8080";

    /// <summary>The acting user the suites pass into every mutating call (audit trail, WI rev 4).</summary>
    protected static readonly SignalChannelActor Actor = new("subject-1", "Jane Admin");

    /// <summary>What <see cref="Actor"/> looks like in a persisted history entry.</summary>
    protected static readonly string ActorAuditString = Actor.ToAuditString();

    protected readonly ICommunicationRepository CommunicationRepository;
    protected readonly ISignalBridgeClient BridgeClient;
    protected readonly IAdapterCache AdapterCache;
    protected readonly ICommunicationEventService CommunicationEventService;
    protected readonly SignalChannelService Service;

    protected SignalChannelServiceTestsBase()
    {
        CommunicationRepository = Substitute.For<ICommunicationRepository>();
        // Default: no tenant stores a SignalChannel. Suites arrange concrete channels per tenant.
        CommunicationRepository.GetSignalChannelsAsync(Arg.Any<string>())
            .Returns(Array.Empty<RtSignalChannel>());

        BridgeClient = Substitute.For<ISignalBridgeClient>();
        // Default: the bridge holds no accounts — the adopt-existing-account check in the
        // register flow finds nothing and the plain register path runs. Suites override this to
        // arrange adoption or the GET cross-check.
        BridgeClient.GetAccountsAsync(Arg.Any<string>()).Returns([]);

        AdapterCache = Substitute.For<IAdapterCache>();
        AdapterCache.GetEnabledTenantIds().Returns([TenantId]);

        CommunicationEventService = Substitute.For<ICommunicationEventService>();

        Service = new SignalChannelService(
            NullLogger<SignalChannelService>.Instance,
            CommunicationRepository,
            BridgeClient,
            AdapterCache,
            CommunicationEventService,
            Microsoft.Extensions.Options.Options.Create(new CommunicationControllerOptions
            {
                SignalBridgeApiUrl = DefaultApiUrl
            }));
    }

    /// <summary>
    /// Stores a channel in the given tenant's mocked repository and returns it.
    /// </summary>
    protected RtSignalChannel ArrangeChannel(RtSignalRegistrationStateEnum state,
        string tenantId = TenantId, string number = Number, string apiUrl = ApiUrl)
    {
        var channel = new RtSignalChannel
        {
            RtId = OctoObjectId.GenerateNewId(),
            CkTypeId = SystemCommunicationCkIds.RtCkSignalChannelTypeId,
            RtWellKnownName = SignalChannelService.ChannelWellKnownName,
            Number = number,
            ApiUrl = apiUrl,
            RegistrationState = state
        };

        CommunicationRepository.GetSignalChannelsAsync(tenantId).Returns([channel]);
        return channel;
    }

    /// <summary>
    /// The channel's registration history materialized into a plain list (newest first, as
    /// stored). AttributeRecordValueList materializes fresh records per read, so suites assert on
    /// this snapshot instead of the live attribute.
    /// </summary>
    protected static List<RtSignalRegistrationEventRecord> History(RtSignalChannel channel)
    {
        return (channel.RegistrationHistory ?? Enumerable.Empty<RtSignalRegistrationEventRecord>()).ToList();
    }

    protected static SignalBridgeException BridgeRejected(string error = "bridge says no")
    {
        return SignalBridgeException.Rejected(400, error);
    }

    /// <summary>
    /// Signal demanding a captcha, with signal-cli's real message text — the rejection the
    /// controller answers as 422 so the Studio wizard shows the captcha step only on demand.
    /// </summary>
    protected static SignalBridgeException BridgeCaptchaRequired()
    {
        return SignalBridgeException.Rejected(400,
            "Captcha required for verification, use --captcha CAPTCHA");
    }

    protected static SignalBridgeException BridgeRateLimited(TimeSpan? retryAfter = null)
    {
        return SignalBridgeException.RateLimited("rate limited", retryAfter);
    }

    protected static SignalBridgeException BridgeUnreachable()
    {
        return SignalBridgeException.Unreachable(ApiUrl, new HttpRequestException("connection refused"));
    }
}
