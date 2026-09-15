using Meshmakers.Octo.Backend.CommunicationControllerServices.Hubs;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Hubs;

/// <summary>
///     AB#4924 — the pool's node descriptors, stored per member and answered per pool.
/// </summary>
/// <remarks>
///     This is the seam that did not exist: the controller had no node descriptors for a pool member
///     at all, so a BORROWER's deploy path had nothing to validate or classify against.
/// </remarks>
internal class AdapterPoolConnectionManagerCapabilitiesTests
{
    private const string LenderTenantId = "lender";
    private const string PoolRtId = "6ad562f3ff7c40ff80275b84";
    private const string OtherPoolRtId = "6ad562f3ff7c40ff80275b85";

    private readonly AdapterPoolConnectionManager _manager = new();

    private static NodeDescriptorDto Descriptor(string name) =>
        new(name, 1, "Trigger", true, false, "{}");

    [Test]
    public async Task ARegisteredMembersDescriptorsAnswerForItsPool()
    {
        _manager.RegisterMember("conn-1", "octo-pool-0", LenderTenantId, PoolRtId,
            [Descriptor("FromCustomThing")], """{"$id":"pool-schema"}""");

        var capabilities = _manager.TryGetPoolCapabilities(LenderTenantId, PoolRtId);

        using var _ = Assert.Multiple();
        await Assert.That(capabilities).IsNotNull();
        await Assert.That(capabilities!.MemberId).IsEqualTo("octo-pool-0");
        await Assert.That(capabilities.NodeDescriptors.Select(d => d.NodeName)).Contains("FromCustomThing");
        await Assert.That(capabilities.PipelineSchemaJson).IsEqualTo("""{"$id":"pool-schema"}""");
    }

    [Test]
    public async Task AnotherPoolsMemberNeverAnswers()
    {
        _manager.RegisterMember("conn-1", "octo-pool-0", LenderTenantId, OtherPoolRtId,
            [Descriptor("FromCustomThing")], "{}");

        // Not a formality: the borrower's LentFromPoolRtId is the only thing that decides which
        // process will run its pipelines, and a manager that answered pool-agnostically would
        // validate a leased pipeline against an unrelated tenant's SDK.
        await Assert.That(_manager.TryGetPoolCapabilities(LenderTenantId, PoolRtId)).IsNull();
    }

    [Test]
    public async Task AMemberThatReportedNoDescriptorsDoesNotAnswer()
    {
        _manager.RegisterMember("conn-1", "octo-pool-0", LenderTenantId, PoolRtId, [], null);

        // "Registered but silent" must read as "nothing known", so the caller degrades to its
        // name-based fallback instead of classifying against an empty set — which would look like
        // "this pool can run nothing".
        await Assert.That(_manager.TryGetPoolCapabilities(LenderTenantId, PoolRtId)).IsNull();
    }

    [Test]
    public async Task ADrainingMemberLosesToALiveOne()
    {
        // During a rolling upgrade the draining member is the OUTGOING version.
        _manager.RegisterMember("conn-old", "aaa-old-member", LenderTenantId, PoolRtId,
            [Descriptor("OldTrigger")], "{}");
        _manager.RegisterMember("conn-new", "zzz-new-member", LenderTenantId, PoolRtId,
            [Descriptor("NewTrigger")], "{}");
        _manager.MarkDraining("conn-old");

        var capabilities = _manager.TryGetPoolCapabilities(LenderTenantId, PoolRtId);

        // Ordinal order alone would have picked "aaa-old-member".
        await Assert.That(capabilities!.MemberId).IsEqualTo("zzz-new-member");
    }

    [Test]
    public async Task TheAnswerIsStableAcrossCalls()
    {
        _manager.RegisterMember("conn-1", "octo-pool-2", LenderTenantId, PoolRtId, [Descriptor("A")], "{}");
        _manager.RegisterMember("conn-2", "octo-pool-0", LenderTenantId, PoolRtId, [Descriptor("B")], "{}");
        _manager.RegisterMember("conn-3", "octo-pool-1", LenderTenantId, PoolRtId, [Descriptor("C")], "{}");

        // A pipeline's persisted execution class must not depend on dictionary enumeration order.
        var first = _manager.TryGetPoolCapabilities(LenderTenantId, PoolRtId);
        var second = _manager.TryGetPoolCapabilities(LenderTenantId, PoolRtId);

        using var _ = Assert.Multiple();
        await Assert.That(first!.MemberId).IsEqualTo("octo-pool-0");
        await Assert.That(second!.MemberId).IsEqualTo("octo-pool-0");
    }
}
