using Meshmakers.Octo.Communication.Contracts.MessageObjects;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.LeaseSchedulerServiceTests;

/// <summary>
///     AB#4924 §9.9 / D4 — the scheduler names the work on the lease it asks for.
/// </summary>
/// <remarks>
///     🔴 <b>The scheduler decides WHICH queued item a lease serves, so it — not the member — names
///     it.</b> A member re-deriving the pipeline from the tenant's queue could pick a different one,
///     and the claim latch would then have latched a different work item than the one that ran: the
///     execution entity would say one pipeline and the process would have executed another.
/// </remarks>
internal class QueuedWorkReachesTheLeaseTests : LeaseSchedulerServiceTestsBase
{
    [Test]
    public async Task TheLeaseRequestCarriesTheQueuedItemsPipelineAndInput()
    {
        var pipelineRtId = OctoObjectId.GenerateNewId();
        ArrangeMembers(1);
        ArrangeQueue(TenantA, Entry("exec-1", minutesAgo: 5, QueuedExecution.BatchClass, pipelineRtId,
            "{\"invoice\":4711}"));

        var granted = await Scheduler.RunSchedulingRoundAsync();

        using var _ = Assert.Multiple();
        await Assert.That(granted).IsEqualTo(1);
        await Assert.That(Requests).Count().IsEqualTo(1);
        await Assert.That(Requests[0].ExecutionId).IsEqualTo("exec-1");
        await Assert.That(Requests[0].PipelineRtId).IsEqualTo(pipelineRtId);
        await Assert.That(Requests[0].PipelineInput).IsEqualTo("{\"invoice\":4711}");
        await Assert.That(Requests[0].BorrowerTenantId).IsEqualTo(TenantA);
    }

    /// <summary>
    ///     Two items of the same tenant: each lease carries <b>its own</b> item's pipeline, not the
    ///     first one's. Cheap, and it is the mistake a single-entry test cannot see.
    /// </summary>
    [Test]
    public async Task EachLeaseCarriesItsOwnItemsWork()
    {
        var first = OctoObjectId.GenerateNewId();
        var second = OctoObjectId.GenerateNewId();
        ArrangeMembers(2);
        ArrangeQueue(TenantA,
            Entry("exec-1", minutesAgo: 9, QueuedExecution.BatchClass, first, "one"),
            Entry("exec-2", minutesAgo: 8, QueuedExecution.BatchClass, second, "two"));

        await Scheduler.RunSchedulingRoundAsync();

        using var _ = Assert.Multiple();
        await Assert.That(Requests).Count().IsEqualTo(2);
        await Assert.That(Requests[0].PipelineRtId).IsEqualTo(first);
        await Assert.That(Requests[0].PipelineInput).IsEqualTo("one");
        await Assert.That(Requests[1].PipelineRtId).IsEqualTo(second);
        await Assert.That(Requests[1].PipelineInput).IsEqualTo("two");
    }

    /// <summary>
    ///     A queued item with no input carries none — an empty string would be a different pipeline
    ///     input than "no input at all", and a node that branches on presence would see the wrong one.
    /// </summary>
    [Test]
    public async Task AnItemQueuedWithoutInputCarriesNull()
    {
        ArrangeMembers(1);
        ArrangeQueue(TenantA, Entry("exec-1", minutesAgo: 5));

        await Scheduler.RunSchedulingRoundAsync();

        await Assert.That(Requests[0].PipelineInput).IsNull();
    }

    /// <summary>AB#5279: the invoker rides the request exactly like the input.</summary>
    [Test]
    public async Task TheLeaseRequestCarriesTheQueuedItemsInvoker()
    {
        ArrangeMembers(1);
        var entry = Entry("exec-1", minutesAgo: 5) with
        {
            Caller = new ExecutePipelineCaller { SubjectId = "user-42", TrustLevel = 2 },
            CallerAccessToken = "enc:v1:xyz"
        };
        ArrangeQueue(TenantA, entry);

        await Scheduler.RunSchedulingRoundAsync();

        using var _ = Assert.Multiple();
        await Assert.That(Requests).Count().IsEqualTo(1);
        await Assert.That(Requests[0].Caller!.SubjectId).IsEqualTo("user-42");
        await Assert.That(Requests[0].CallerAccessToken).IsEqualTo("enc:v1:xyz");
    }
}
