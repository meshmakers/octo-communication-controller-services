using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services;

internal class ReconciliationGraceTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 10, 15, 59, 49, TimeSpan.Zero);
    private static readonly TimeSpan Grace = TimeSpan.FromMinutes(5);

    [Test]
    public async Task NoRegistration_BeforeThreeIntervals_IsNotOver()
    {
        await Assert.That(ReconciliationGrace.IsOver(Start + TimeSpan.FromMinutes(14), Start, null, Grace)).IsFalse();
    }

    [Test]
    public async Task NoRegistration_AfterThreeIntervals_IsOver()
    {
        await Assert.That(ReconciliationGrace.IsOver(Start + TimeSpan.FromMinutes(15), Start, null, Grace)).IsTrue();
    }

    [Test]
    public async Task Registration_LessThanOneIntervalAgo_IsNotOver()
    {
        var last = Start + TimeSpan.FromMinutes(6);

        await Assert.That(ReconciliationGrace.IsOver(last + TimeSpan.FromMinutes(4), Start, last, Grace)).IsFalse();
    }

    [Test]
    public async Task Registration_OneIntervalAgo_IsOver_EvenBeforeTheCap()
    {
        // Operators registered right after the start, nothing since: no reason to wait for the cap.
        var last = Start + TimeSpan.FromSeconds(10);

        await Assert.That(ReconciliationGrace.IsOver(last + Grace, Start, last, Grace)).IsTrue();
    }

    [Test]
    public async Task Registration_JustNow_AfterTheCap_IsOver()
    {
        var now = Start + TimeSpan.FromMinutes(30);

        await Assert.That(ReconciliationGrace.IsOver(now, Start, now, Grace)).IsTrue();
    }
}
