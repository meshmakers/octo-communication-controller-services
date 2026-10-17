using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services;

internal class NotificationCronTests
{
    [Test]
    [Arguments("0 * * * * ?")]
    [Arguments("0 */5 * * * *")]
    [Arguments("30 0 8 * * MON-FRI")]
    [Arguments("0 0 12 * * ? 2026")]
    [Arguments("*/5 * * * *")]
    public async Task AtMostOncePerMinute_IsAccepted(string cron)
    {
        await Assert.That(NotificationCron.FiresAtMostOncePerMinute(cron)).IsTrue();
    }

    [Test]
    [Arguments("* * * * * *")]
    [Arguments("*/10 * * * * ?")]
    [Arguments("0,30 * * * * ?")]
    [Arguments("0-5 * * * * *")]
    [Arguments("60 * * * * *")]
    [Arguments("0 * *")]
    public async Task MoreThanOncePerMinuteOrMalformed_IsRefused(string cron)
    {
        await Assert.That(NotificationCron.FiresAtMostOncePerMinute(cron)).IsFalse();
    }
}
