using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Backend.CommunicationControllerServices.TenantApi.v1.Controllers;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v3;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Controllers;

internal class PipelineControllerDebugTests
{
    private const string TenantId = "acme";

    private static (PipelineController sut, ICommunicationRepository repo, IAdapterService adapters,
        ICommunicationEventService events) CreateSut()
    {
        var repo = Substitute.For<ICommunicationRepository>();
        var triggers = Substitute.For<ITriggerManagementService>();
        var adapters = Substitute.For<IAdapterService>();
        var events = Substitute.For<ICommunicationEventService>();
        var sut = new PipelineController(NullLogger<PipelineController>.Instance, triggers, adapters, repo, events);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.RouteValues["tenantId"] = TenantId;
        sut.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return (sut, repo, adapters, events);
    }

    [Test]
    public async Task SetPipelineDebugging_AppliedLive_ReturnsOkAndWritesAuditEvent()
    {
        var (sut, _, adapters, events) = CreateSut();
        var pipelineRtId = OctoObjectId.GenerateNewId();
        adapters.SetPipelineDebuggingAsync(TenantId, Arg.Any<RtEntityId>(), true).Returns(true);

        var result = await sut.SetPipelineDebugging(pipelineRtId, new SetPipelineDebugRequestDto(true));

        var ok = result as OkObjectResult;
        await Assert.That(ok).IsNotNull();
        var dto = ok!.Value as SetPipelineDebugResultDto;
        await Assert.That(dto).IsNotNull();
        await Assert.That(dto!.Enabled).IsTrue();
        await Assert.That(dto.AppliedToRunningAdapter).IsTrue();
        await events.Received(1).StoreInformationEventAsync(TenantId,
            Arg.Is<string>(m => m.Contains("debugging enabled") && m.Contains("(source: User)")));
    }

    [Test]
    public async Task SetPipelineDebugging_AdapterOffline_ReturnsOkWithAppliedFalse()
    {
        var (sut, _, adapters, _) = CreateSut();
        var pipelineRtId = OctoObjectId.GenerateNewId();
        adapters.SetPipelineDebuggingAsync(TenantId, Arg.Any<RtEntityId>(), false).Returns(false);

        var result = await sut.SetPipelineDebugging(pipelineRtId, new SetPipelineDebugRequestDto(false));

        var ok = result as OkObjectResult;
        await Assert.That(ok).IsNotNull();
        var dto = ok!.Value as SetPipelineDebugResultDto;
        await Assert.That(dto!.Enabled).IsFalse();
        await Assert.That(dto.AppliedToRunningAdapter).IsFalse();
    }

    [Test]
    public async Task SetPipelineDebugging_AdapterServiceException_ReturnsNotFound()
    {
        var (sut, _, adapters, _) = CreateSut();
        var pipelineRtId = OctoObjectId.GenerateNewId();
        adapters.SetPipelineDebuggingAsync(TenantId, Arg.Any<RtEntityId>(), true)
            .ThrowsAsync(AdapterServiceException.PipelineNotFound(TenantId,
                new RtEntityId(SystemCommunicationCkIds.RtCkPipelineTypeId, pipelineRtId)));

        var result = await sut.SetPipelineDebugging(pipelineRtId, new SetPipelineDebugRequestDto(true));

        await Assert.That(result is NotFoundObjectResult).IsTrue();
    }

    [Test]
    public async Task GetPipelineDebugging_ReturnsPersistedState()
    {
        var (sut, repo, _, _) = CreateSut();
        var rtPipeline = RtEntityCreator.CreatePipeline();
        rtPipeline.IsDebuggingEnabled = true;
        repo.GetPipelineAsync(TenantId, Arg.Any<RtEntityId>()).Returns(rtPipeline);

        var result = await sut.GetPipelineDebugging(rtPipeline.RtId);

        var ok = result as OkObjectResult;
        await Assert.That(ok).IsNotNull();
        var dto = ok!.Value as PipelineDebugStateDto;
        await Assert.That(dto).IsNotNull();
        await Assert.That(dto!.Enabled).IsTrue();
    }

    [Test]
    public async Task GetPipelineDebugging_PipelineNotFound_ReturnsNotFound()
    {
        var (sut, repo, _, _) = CreateSut();
        var pipelineRtId = OctoObjectId.GenerateNewId();
        repo.GetPipelineAsync(TenantId, Arg.Any<RtEntityId>()).Returns((RtPipeline?)null);

        var result = await sut.GetPipelineDebugging(pipelineRtId);

        await Assert.That(result is NotFoundObjectResult).IsTrue();
    }
}
