using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Backend.CommunicationControllerServices.TenantApi.v1.Controllers;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Controllers;

internal class NotificationsControllerTests
{
    private const string TenantId = "acme";
    private static readonly OctoObjectId SubscriptionRtId = OctoObjectId.GenerateNewId();

    private readonly INotificationScheduleService _schedule = Substitute.For<INotificationScheduleService>();
    private readonly NotificationsController _sut;

    public NotificationsControllerTests()
    {
        _sut = new NotificationsController(NullLogger<NotificationsController>.Instance, _schedule);
        var httpContext = new DefaultHttpContext();
        httpContext.Request.RouteValues["tenantId"] = TenantId;
        _sut.ControllerContext = new ControllerContext { HttpContext = httpContext };
    }

    [Test]
    public async Task DeploySubscription_Succeeds_Returns204()
    {
        var result = await _sut.DeploySubscription(SubscriptionRtId.ToString());

        await Assert.That(result).IsTypeOf<NoContentResult>();
        await _schedule.Received(1).DeploySubscriptionAsync(TenantId, SubscriptionRtId);
    }

    [Test]
    public async Task DeploySubscription_InvalidRtId_Returns400WithoutCallingTheService()
    {
        var result = await _sut.DeploySubscription("not-an-id");

        await Assert.That(result).IsTypeOf<BadRequestObjectResult>();
        await _schedule.DidNotReceive().DeploySubscriptionAsync(Arg.Any<string>(), Arg.Any<OctoObjectId>());
    }

    [Test]
    public async Task DeploySubscription_UnknownSubscription_Returns404()
    {
        _schedule.DeploySubscriptionAsync(TenantId, SubscriptionRtId)
            .ThrowsAsync(NotificationScheduleServiceException.SubscriptionNotFound(TenantId, SubscriptionRtId));

        var result = await _sut.DeploySubscription(SubscriptionRtId.ToString());

        await Assert.That(result).IsTypeOf<NotFoundObjectResult>();
    }

    [Test]
    public async Task DeploySubscription_EngineCannotRun_Returns409WithTheReason()
    {
        _schedule.DeploySubscriptionAsync(TenantId, SubscriptionRtId)
            .ThrowsAsync(NotificationScheduleServiceException.EngineMissing(TenantId));

        var result = await _sut.DeploySubscription(SubscriptionRtId.ToString());

        var conflict = result as ConflictObjectResult;
        await Assert.That(conflict).IsNotNull();
        await Assert.That(((ErrorResponse)conflict!.Value!).ErrorMessage).Contains("System.Communication.Notifications");
    }

    [Test]
    public async Task SyncSchedule_IncompleteSchedule_Returns503()
    {
        _schedule.UpdateScheduleAsync(TenantId).ThrowsAsync(NotificationScheduleServiceException.ScheduleIncomplete(
            TenantId, [OctoObjectId.GenerateNewId()], new InvalidOperationException("bus down")));

        var result = await _sut.SyncSchedule();

        await Assert.That((result as ObjectResult)!.StatusCode).IsEqualTo(StatusCodes.Status503ServiceUnavailable);
    }

    [Test]
    public async Task SyncSchedule_ForOnePreference_SyncsOnlyThatPreference()
    {
        var preferenceRtId = OctoObjectId.GenerateNewId();

        var result = await _sut.SyncSchedule(preferenceRtId.ToString());

        await Assert.That(result).IsTypeOf<NoContentResult>();
        await _schedule.Received(1).SyncPreferenceAsync(TenantId, preferenceRtId);
        await _schedule.DidNotReceive().UpdateScheduleAsync(Arg.Any<string>());
    }

    [Test]
    public async Task SyncSchedule_InvalidPreferenceRtId_Returns400WithoutCallingTheService()
    {
        var result = await _sut.SyncSchedule("not-an-id");

        await Assert.That(result).IsTypeOf<BadRequestObjectResult>();
        await _schedule.DidNotReceive().SyncPreferenceAsync(Arg.Any<string>(), Arg.Any<OctoObjectId>());
    }

    [Test]
    public async Task UndeploySubscription_UnexpectedFailure_Returns500()
    {
        _schedule.UndeploySubscriptionAsync(TenantId, SubscriptionRtId)
            .ThrowsAsync(new InvalidOperationException("mongo down"));

        var result = await _sut.UndeploySubscription(SubscriptionRtId.ToString());

        await Assert.That((result as ObjectResult)!.StatusCode).IsEqualTo(StatusCodes.Status500InternalServerError);
    }
}
