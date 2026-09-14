using Meshmakers.Octo.Backend.CommunicationControllerServices.Hubs;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Microsoft.AspNetCore.SignalR;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.LeaseServiceTests;

/// <summary>
///     AB#4924 §13 — the per-tenant leasing kill switch, <b>grant</b> half.
/// </summary>
/// <remarks>
///     <para>
///         🔴 <b>Why the grant path and not only the enqueue.</b> A switch that stopped new enqueues
///         alone would leave whatever is already queued draining for as long as it takes after somebody
///         turned leasing off. That is not what an operator means by "off", and an operator who has just
///         switched something off in an incident is the least likely person to be told to wait.
///     </para>
///     <para>
///         🔴 <b>Whose switch it is: both tenants'.</b> The flag means a different thing on each — on
///         the lender "this tenant's pools hand their members out", on the borrower "this tenant's
///         <c>Leased</c> adapters get scheduled". Lending is the lender's capability and borrowing is
///         the borrower's, and neither tenant can assert the other's, so one <c>true</c> is never
///         enough. That is concept §13's "both the lender and the borrower tenant must have it on",
///         made enforceable.
///     </para>
///     <para>
///         🔴 <b>What happens to work already queued: it is HELD.</b> The refusal leaves the execution
///         <c>Queued</c> — nothing is claimed, nothing is cancelled, nothing is lost — and it stays
///         visible in all three queue surfaces. Switching leasing back on resumes it in its original
///         order. Draining would mean "off" still runs the next hour of work; cancelling would destroy
///         work the operator never asked to lose, and a queue entry belongs to the borrower rather than
///         to whoever flipped the switch.
///     </para>
/// </remarks>
internal class LeasingEnabledGateTests : LeaseServiceTestsBase
{
    [Test]
    public async Task WithLeasingOffOnTheLendingTenant_NoLeaseIsGranted()
    {
        ArrangeGrantableLease();
        ArrangeProjectablePipeline();
        LifecycleConfiguration.IsLeasingEnabledAsync(LenderTenantId).Returns(false);

        var result = await LeaseService.GrantLeaseAsync(LenderTenantId, PoolRtId, AWorkRequest());

        using var _ = Assert.Multiple();
        await Assert.That(result.Granted).IsFalse();
        await Assert.That(result.StatusMessage).Contains(LenderTenantId);
        await AssertNothingHappenedAsync();
    }

    [Test]
    public async Task WithLeasingOffOnTheBorrowingTenant_NoLeaseIsGranted()
    {
        ArrangeGrantableLease();
        ArrangeProjectablePipeline();
        LifecycleConfiguration.IsLeasingEnabledAsync(BorrowerTenantId).Returns(false);

        var result = await LeaseService.GrantLeaseAsync(LenderTenantId, PoolRtId, AWorkRequest());

        using var _ = Assert.Multiple();
        await Assert.That(result.Granted).IsFalse();
        await Assert.That(result.StatusMessage).Contains(BorrowerTenantId);
        await AssertNothingHappenedAsync();
    }

    /// <summary>
    ///     🔴 The gate runs <b>before</b> anything reads a credential. A tenant whose leasing is off
    ///     must not have its borrower's client secret decrypted on the way to a refusal — the order of
    ///     least trust that the rest of <c>GrantLeaseAsync</c> already follows.
    /// </summary>
    [Test]
    public async Task TheGateRunsBeforeAnyCredentialIsRead()
    {
        ArrangeGrantableLease();
        LifecycleConfiguration.IsLeasingEnabledAsync(LenderTenantId).Returns(false);

        await LeaseService.GrantLeaseAsync(LenderTenantId, PoolRtId, AWorkRequest());

        using var _ = Assert.Multiple();
        await ServiceAccountResolver.DidNotReceiveWithAnyArgs().GetAdapterDefaultAsync(default!, default!);
        EncryptionService.DidNotReceiveWithAnyArgs().Decrypt(default!);
    }

    /// <summary>
    ///     Both halves on is the only combination that grants. Stated as its own test so the two
    ///     refusals above cannot both pass because the gate simply always refuses.
    /// </summary>
    [Test]
    public async Task WithLeasingOnForBothTenants_TheLeaseIsGranted()
    {
        ArrangeGrantableLease();
        ArrangeProjectablePipeline();

        var result = await LeaseService.GrantLeaseAsync(LenderTenantId, PoolRtId, AWorkRequest());

        await Assert.That(result.Granted).IsTrue();
    }

    /// <summary>
    ///     "Held" spelled out: the member stays idle, nothing is pushed at it, and nothing about the
    ///     queued work item is written — no claim, no cancellation.
    /// </summary>
    private async Task AssertNothingHappenedAsync()
    {
        await Assert.That(MemberProxy.ReceivedCalls()
            .Any(c => c.GetMethodInfo().Name == nameof(IClientProxy.SendCoreAsync))).IsFalse();
        await Assert.That(ConnectionManager.GetMembers(LenderTenantId, PoolRtId.ToString())
            .All(m => m.IsAvailable)).IsTrue();
        await CommunicationRepository.DidNotReceiveWithAnyArgs()
            .TryClaimQueuedExecutionAsync(default!, default!, default);
        await CommunicationRepository.DidNotReceiveWithAnyArgs()
            .TryCancelQueuedExecutionAsync(default!, default!, default);
    }
}
