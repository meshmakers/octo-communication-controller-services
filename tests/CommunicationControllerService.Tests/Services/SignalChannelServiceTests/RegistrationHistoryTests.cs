using Meshmakers.Octo.Backend.CommunicationControllerServices.Models;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v3;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.SignalChannelServiceTests;

/// <summary>
/// AB#5143 registration audit trail (WI rev 4): every register/verify/delete attempt — including
/// bridge failures the API answers as 4xx/429 — is appended server-side to
/// <c>SignalChannel.RegistrationHistory</c> with UTC timestamp, acting user, action and outcome
/// detail; newest first, capped at the newest 50; projected as <c>history</c> on the DTO. A
/// successful delete erases the definition including its history (fresh definition = fresh
/// history).
/// </summary>
internal class RegistrationHistoryTests : SignalChannelServiceTestsBase
{
    private const string Code = "123456";

    [Test]
    public async Task RegisterSuccess_AppendsRegisterRequested_WithActorAndUtcTimestamp()
    {
        RtSignalChannel? saved = null;
        await CommunicationRepository.SaveSignalChannelAsync(TenantId, Arg.Do<RtSignalChannel>(c => saved = c),
            Arg.Any<bool>());
        var before = DateTime.UtcNow;

        var dto = await Service.RegisterAsync(TenantId, Actor, Number, ApiUrl, null);

        var history = History(saved!);
        await Assert.That(history.Count).IsEqualTo(1);
        await Assert.That(history[0].Action).IsEqualTo(RtSignalRegistrationActionEnum.RegisterRequested);
        await Assert.That(history[0].User).IsEqualTo(ActorAuditString);
        await Assert.That(history[0].Detail).Contains(Number);
        await Assert.That(history[0].At).IsGreaterThanOrEqualTo(before);
        await Assert.That(history[0].At).IsLessThanOrEqualTo(DateTime.UtcNow);

        // The mutation answer already projects the trail.
        await Assert.That(dto.History.Count).IsEqualTo(1);
        await Assert.That(dto.History[0].Action)
            .IsEqualTo(nameof(RtSignalRegistrationActionEnum.RegisterRequested));
    }

    [Test]
    public async Task RegisterBridgeRejected_PersistsRegisterFailedExactlyOnce_Despite400()
    {
        // Snapshot the history at every save: the claim save happens BEFORE the bridge call, the
        // failure save must carry the RegisterFailed entry exactly once on top of it.
        var snapshots = new List<List<RtSignalRegistrationEventRecord>>();
        await CommunicationRepository.SaveSignalChannelAsync(TenantId,
            Arg.Do<RtSignalChannel>(c => snapshots.Add(History(c))), Arg.Any<bool>());
        BridgeClient.RegisterAsync(ApiUrl, Number, null).ThrowsAsync(BridgeRejected("number blocked"));

        await Assert.ThrowsAsync<SignalChannelServiceException>(
            () => Service.RegisterAsync(TenantId, Actor, Number, ApiUrl, null));

        await Assert.That(snapshots.Count).IsEqualTo(2);
        // Claim save: the attempt is already recorded.
        await Assert.That(snapshots[0].Count).IsEqualTo(1);
        await Assert.That(snapshots[0][0].Action).IsEqualTo(RtSignalRegistrationActionEnum.RegisterRequested);
        // Failure save: newest first, the failure appended exactly once.
        await Assert.That(snapshots[1].Count).IsEqualTo(2);
        await Assert.That(snapshots[1][0].Action).IsEqualTo(RtSignalRegistrationActionEnum.RegisterFailed);
        await Assert.That(snapshots[1][0].Detail).Contains("number blocked");
        await Assert.That(snapshots[1][1].Action).IsEqualTo(RtSignalRegistrationActionEnum.RegisterRequested);
        await Assert.That(snapshots[1]
                .Count(e => e.Action == RtSignalRegistrationActionEnum.RegisterFailed))
            .IsEqualTo(1);
    }

    [Test]
    public async Task RegisterRateLimited_EntryCarriesRateLimitDetail_Despite429()
    {
        RtSignalChannel? saved = null;
        await CommunicationRepository.SaveSignalChannelAsync(TenantId, Arg.Do<RtSignalChannel>(c => saved = c),
            Arg.Any<bool>());
        BridgeClient.RegisterAsync(ApiUrl, Number, null)
            .ThrowsAsync(BridgeRateLimited(TimeSpan.FromSeconds(30)));

        await Assert.ThrowsAsync<SignalChannelServiceException>(
            () => Service.RegisterAsync(TenantId, Actor, Number, ApiUrl, null));

        var history = History(saved!);
        await Assert.That(history[0].Action).IsEqualTo(RtSignalRegistrationActionEnum.RegisterFailed);
        await Assert.That(history[0].Detail).Contains("rate limited");
        await Assert.That(history[0].Detail).Contains("30");
    }

    [Test]
    public async Task VerifySuccess_AppendsCodeVerified_OnTopOfExistingTrail()
    {
        var channel = ArrangeChannel(RtSignalRegistrationStateEnum.CodePending);
        SeedHistory(channel, RtSignalRegistrationActionEnum.RegisterRequested);

        await Service.VerifyAsync(TenantId, Actor, Code);

        var history = History(channel);
        await Assert.That(history.Count).IsEqualTo(2);
        await Assert.That(history[0].Action).IsEqualTo(RtSignalRegistrationActionEnum.CodeVerified);
        await Assert.That(history[0].Detail).IsEqualTo("registered");
        await Assert.That(history[0].User).IsEqualTo(ActorAuditString);
        await Assert.That(history[1].Action).IsEqualTo(RtSignalRegistrationActionEnum.RegisterRequested);
    }

    [Test]
    public async Task VerifyBridgeRejected_PersistsVerifyFailed_Despite400()
    {
        var channel = ArrangeChannel(RtSignalRegistrationStateEnum.CodePending);
        BridgeClient.VerifyAsync(ApiUrl, Number, Code).ThrowsAsync(BridgeRejected("wrong code"));

        await Assert.ThrowsAsync<SignalChannelServiceException>(
            () => Service.VerifyAsync(TenantId, Actor, Code));

        // The failed attempt is saved even though the endpoint answers 400.
        await CommunicationRepository.Received(1).SaveSignalChannelAsync(TenantId, channel, false);
        var history = History(channel);
        await Assert.That(history.Count).IsEqualTo(1);
        await Assert.That(history[0].Action).IsEqualTo(RtSignalRegistrationActionEnum.VerifyFailed);
        await Assert.That(history[0].Detail).Contains("wrong code");
    }

    [Test]
    public async Task DeleteRepositoryFailure_PersistsDeleteFailed_AndRethrows()
    {
        var channel = ArrangeChannel(RtSignalRegistrationStateEnum.CodePending);
        CommunicationRepository.DeleteSignalChannelAsync(TenantId, Arg.Any<RtEntityId>())
            .ThrowsAsync(new InvalidOperationException("delete blew up"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Service.DeleteChannelAsync(TenantId, Actor));

        // The definition survives the failed delete and carries the audited attempt.
        await CommunicationRepository.Received(1).SaveSignalChannelAsync(TenantId, channel, false);
        var history = History(channel);
        await Assert.That(history.Count).IsEqualTo(1);
        await Assert.That(history[0].Action).IsEqualTo(RtSignalRegistrationActionEnum.DeleteFailed);
        await Assert.That(history[0].Detail).Contains("delete blew up");
    }

    [Test]
    public async Task DeleteSuccess_WritesNoHistory_TrailDiesWithTheDefinition()
    {
        // Fresh definition = fresh history: a successful delete erases the definition INCLUDING
        // its trail, so no history write happens on the way out.
        ArrangeChannel(RtSignalRegistrationStateEnum.Registered);

        await Service.DeleteChannelAsync(TenantId, Actor);

        await CommunicationRepository.Received(1)
            .DeleteSignalChannelAsync(TenantId, Arg.Any<RtEntityId>());
        await CommunicationRepository.DidNotReceiveWithAnyArgs()
            .SaveSignalChannelAsync(default!, default!, default);
    }

    [Test]
    public async Task HistoryCap_KeepsTheNewest50_TrimsTheOldest()
    {
        var channel = ArrangeChannel(RtSignalRegistrationStateEnum.Failed);
        // Pre-fill the cap: 50 entries, newest first ("old-0" newest ... "old-49" oldest).
        SeedHistory(channel, Enumerable.Range(0, SignalChannelService.MaxHistoryEntries)
            .Select(i => NewEvent(RtSignalRegistrationActionEnum.RegisterFailed, $"old-{i}"))
            .ToList());

        await Service.RegisterAsync(TenantId, Actor, Number, null, null);

        var history = History(channel);
        await Assert.That(history.Count).IsEqualTo(SignalChannelService.MaxHistoryEntries);
        await Assert.That(history[0].Action).IsEqualTo(RtSignalRegistrationActionEnum.RegisterRequested);
        await Assert.That(history[1].Detail).IsEqualTo("old-0");
        await Assert.That(history[^1].Detail).IsEqualTo("old-48");
        await Assert.That(history.Any(e => e.Detail == "old-49")).IsFalse();
    }

    [Test]
    public async Task GetChannel_ProjectsHistoryNewestFirst()
    {
        var channel = ArrangeChannel(RtSignalRegistrationStateEnum.Registered);
        var newest = NewEvent(RtSignalRegistrationActionEnum.CodeVerified, "registered");
        var oldest = NewEvent(RtSignalRegistrationActionEnum.RegisterRequested, $"number '{Number}'");
        SeedHistory(channel, [newest, oldest]);
        BridgeClient.GetAccountsAsync(ApiUrl).Returns([Number]);

        var dto = await Service.GetChannelAsync(TenantId);

        await Assert.That(dto!.History.Count).IsEqualTo(2);
        await Assert.That(dto.History[0].Action).IsEqualTo(nameof(RtSignalRegistrationActionEnum.CodeVerified));
        await Assert.That(dto.History[0].Detail).IsEqualTo("registered");
        await Assert.That(dto.History[0].User).IsEqualTo(ActorAuditString);
        await Assert.That(dto.History[0].At).IsEqualTo(newest.At);
        await Assert.That(dto.History[1].Action)
            .IsEqualTo(nameof(RtSignalRegistrationActionEnum.RegisterRequested));
    }

    [Test]
    [Arguments("subject-1", "Jane Admin", "Jane Admin (subject-1)")]
    [Arguments("subject-1", null, "subject-1")]
    [Arguments(null, "Jane Admin", "Jane Admin")]
    [Arguments(null, null, "unknown")]
    [Arguments("same", "same", "same")]
    public async Task ActorAuditString_FormatsClaims(string? subjectId, string? name, string expected)
    {
        var actor = new SignalChannelActor(subjectId, name);

        await Assert.That(actor.ToAuditString()).IsEqualTo(expected);
    }

    [Test]
    public async Task ServiceOnlyActor_SubjectIdRecorded()
    {
        RtSignalChannel? saved = null;
        await CommunicationRepository.SaveSignalChannelAsync(TenantId, Arg.Do<RtSignalChannel>(c => saved = c),
            Arg.Any<bool>());

        await Service.RegisterAsync(TenantId, new SignalChannelActor("service-client", null), Number, ApiUrl,
            null);

        await Assert.That(History(saved!)[0].User).IsEqualTo("service-client");
    }

    private static RtSignalRegistrationEventRecord NewEvent(RtSignalRegistrationActionEnum action,
        string? detail)
    {
        return new RtSignalRegistrationEventRecord
        {
            At = DateTime.UtcNow,
            User = ActorAuditString,
            Action = action,
            Detail = detail
        };
    }

    private static void SeedHistory(RtSignalChannel channel, RtSignalRegistrationActionEnum action)
    {
        SeedHistory(channel, [NewEvent(action, null)]);
    }

    private static void SeedHistory(RtSignalChannel channel, List<RtSignalRegistrationEventRecord> events)
    {
        channel.RegistrationHistory = new AttributeRecordValueList<RtSignalRegistrationEventRecord>(
            events.Cast<RtRecord>().ToList());
    }
}
