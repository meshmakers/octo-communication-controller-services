using Meshmakers.Octo.Backend.CommunicationControllerServices.Hubs;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Models;
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

/// <summary>
///     Pins the branching of <c>PipelineController.MovePipelinesToAdapter</c> —
///     bulk reassignment of pipelines from their current adapter to a new
///     target adapter (Studio's "move pipeline to another adapter" flow).
///     Coverage is the controller layer: status codes, per-pipeline result
///     mapping, audit event hookup, redeploy semantics. Repository-side
///     behaviour (assoc swap, CkTypeId match check) lives in the integration
///     tests.
/// </summary>
internal class PipelineControllerMoveToAdapterTests
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

    private static RtEntityId AdapterRtEntityId(OctoObjectId rtId) =>
        new(SystemCommunicationCkIds.RtCkAdapterTypeId, rtId);

    [Test]
    public async Task MovePipelines_EmptyList_Returns400()
    {
        var (sut, _, _, events) = CreateSut();

        var result = await sut.MovePipelinesToAdapter(
            new MovePipelinesToAdapterRequestDto(new List<string>(), OctoObjectId.GenerateNewId().ToString(), false));

        await Assert.That(result is BadRequestObjectResult).IsTrue();
        await events.DidNotReceive().StoreInformationEventAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    [Test]
    public async Task MovePipelines_InvalidTargetAdapterId_Returns400()
    {
        var (sut, _, _, _) = CreateSut();

        var result = await sut.MovePipelinesToAdapter(
            new MovePipelinesToAdapterRequestDto(
                new List<string> { OctoObjectId.GenerateNewId().ToString() },
                "not-an-octo-object-id",
                false));

        await Assert.That(result is BadRequestObjectResult).IsTrue();
    }

    [Test]
    public async Task MovePipelines_ValidSingleMove_ReturnsOkSuccessAndWritesAuditEvent()
    {
        var (sut, repo, adapters, events) = CreateSut();
        var pipelineRtId = OctoObjectId.GenerateNewId();
        var oldAdapter = OctoObjectId.GenerateNewId();
        var newAdapter = OctoObjectId.GenerateNewId();
        repo.MovePipelineToAdapterAsync(TenantId, pipelineRtId, newAdapter)
            .Returns(new PipelineMoveResult(pipelineRtId, AdapterRtEntityId(oldAdapter),
                AdapterRtEntityId(newAdapter)));

        var result = await sut.MovePipelinesToAdapter(
            new MovePipelinesToAdapterRequestDto(
                new List<string> { pipelineRtId.ToString() },
                newAdapter.ToString(),
                false));

        var ok = result as OkObjectResult;
        await Assert.That(ok).IsNotNull();
        var response = ok!.Value as MovePipelinesToAdapterResponseDto;
        await Assert.That(response).IsNotNull();
        await Assert.That(response!.Results.Count).IsEqualTo(1);
        await Assert.That(response.Results[0].Success).IsTrue();
        await Assert.That(response.Results[0].OldAdapterRtId).IsEqualTo(oldAdapter.ToString());
        await Assert.That(response.Results[0].NewAdapterRtId).IsEqualTo(newAdapter.ToString());
        await Assert.That(response.Results[0].ErrorMessage).IsNull();
        await events.Received(1).StoreInformationEventAsync(TenantId,
            Arg.Is<string>(m => m.Contains("moved from adapter") && m.Contains("(source: User)")));
        // Redeploy off → deploy NOT called
        await adapters.DidNotReceive().DeployPipelineAsync(Arg.Any<string>(), Arg.Any<RtEntityId>(),
            Arg.Any<RtEntityId>(), Arg.Any<string?>());
    }

    [Test]
    public async Task MovePipelines_WithRedeploy_TriggersDeployOnTargetAdapter()
    {
        var (sut, repo, adapters, _) = CreateSut();
        var pipelineRtId = OctoObjectId.GenerateNewId();
        var oldAdapter = OctoObjectId.GenerateNewId();
        var newAdapter = OctoObjectId.GenerateNewId();
        var newAdapterRtEntityId = AdapterRtEntityId(newAdapter);
        repo.MovePipelineToAdapterAsync(TenantId, pipelineRtId, newAdapter)
            .Returns(new PipelineMoveResult(pipelineRtId, AdapterRtEntityId(oldAdapter), newAdapterRtEntityId));

        var result = await sut.MovePipelinesToAdapter(
            new MovePipelinesToAdapterRequestDto(
                new List<string> { pipelineRtId.ToString() },
                newAdapter.ToString(),
                true));

        var ok = result as OkObjectResult;
        var response = (ok!.Value as MovePipelinesToAdapterResponseDto)!;
        await Assert.That(response.Results[0].Success).IsTrue();
        await Assert.That(response.Results[0].ErrorMessage).IsNull();
        await adapters.Received(1).DeployPipelineAsync(TenantId, newAdapterRtEntityId,
            Arg.Is<RtEntityId>(e => e.RtId.Equals(pipelineRtId)), null);
    }

    [Test]
    public async Task MovePipelines_RedeployFails_MoveStaysSuccessButWarningInErrorMessage()
    {
        var (sut, repo, adapters, _) = CreateSut();
        var pipelineRtId = OctoObjectId.GenerateNewId();
        var oldAdapter = OctoObjectId.GenerateNewId();
        var newAdapter = OctoObjectId.GenerateNewId();
        repo.MovePipelineToAdapterAsync(TenantId, pipelineRtId, newAdapter)
            .Returns(new PipelineMoveResult(pipelineRtId, AdapterRtEntityId(oldAdapter),
                AdapterRtEntityId(newAdapter)));
        adapters.DeployPipelineAsync(Arg.Any<string>(), Arg.Any<RtEntityId>(), Arg.Any<RtEntityId>(),
                Arg.Any<string?>())
            .ThrowsAsync(new InvalidOperationException("adapter offline"));

        var result = await sut.MovePipelinesToAdapter(
            new MovePipelinesToAdapterRequestDto(
                new List<string> { pipelineRtId.ToString() },
                newAdapter.ToString(),
                true));

        var response = ((result as OkObjectResult)!.Value as MovePipelinesToAdapterResponseDto)!;
        await Assert.That(response.Results[0].Success).IsTrue();
        await Assert.That(response.Results[0].ErrorMessage).IsNotNull();
        await Assert.That(response.Results[0].ErrorMessage!).Contains("redeploy");
        await Assert.That(response.Results[0].ErrorMessage!).Contains("adapter offline");
    }

    [Test]
    public async Task MovePipelines_AlreadyOnTarget_NoOpButReportsSuccess()
    {
        var (sut, repo, adapters, _) = CreateSut();
        var pipelineRtId = OctoObjectId.GenerateNewId();
        var sameAdapter = OctoObjectId.GenerateNewId();
        var sameAdapterRtEntityId = AdapterRtEntityId(sameAdapter);
        repo.MovePipelineToAdapterAsync(TenantId, pipelineRtId, sameAdapter)
            .Returns(new PipelineMoveResult(pipelineRtId, sameAdapterRtEntityId, sameAdapterRtEntityId));

        var result = await sut.MovePipelinesToAdapter(
            new MovePipelinesToAdapterRequestDto(
                new List<string> { pipelineRtId.ToString() },
                sameAdapter.ToString(),
                true));   // redeploy=true should still be skipped because no actual move

        var response = ((result as OkObjectResult)!.Value as MovePipelinesToAdapterResponseDto)!;
        await Assert.That(response.Results[0].Success).IsTrue();
        await adapters.DidNotReceive().DeployPipelineAsync(Arg.Any<string>(), Arg.Any<RtEntityId>(),
            Arg.Any<RtEntityId>(), Arg.Any<string?>());
    }

    [Test]
    public async Task MovePipelines_RepositoryThrows_ReportsPipelineLevelFailure()
    {
        var (sut, repo, _, _) = CreateSut();
        var pipelineRtId = OctoObjectId.GenerateNewId();
        var newAdapter = OctoObjectId.GenerateNewId();
        repo.MovePipelineToAdapterAsync(TenantId, pipelineRtId, newAdapter)
            .ThrowsAsync(CommunicationRepositoryException.AdapterTypeMismatchForMove(TenantId, pipelineRtId,
                SystemCommunicationCkIds.RtCkAdapterTypeId, SystemCommunicationCkIds.RtCkAdapterTypeId));

        var result = await sut.MovePipelinesToAdapter(
            new MovePipelinesToAdapterRequestDto(
                new List<string> { pipelineRtId.ToString() },
                newAdapter.ToString(),
                false));

        var response = ((result as OkObjectResult)!.Value as MovePipelinesToAdapterResponseDto)!;
        await Assert.That(response.Results[0].Success).IsFalse();
        await Assert.That(response.Results[0].ErrorMessage).IsNotNull();
    }

    [Test]
    public async Task MovePipelines_MixedBulk_KeepsBatchRunningAndReportsPerPipeline()
    {
        var (sut, repo, _, _) = CreateSut();
        var validPipeline = OctoObjectId.GenerateNewId();
        var failingPipeline = OctoObjectId.GenerateNewId();
        var newAdapter = OctoObjectId.GenerateNewId();
        repo.MovePipelineToAdapterAsync(TenantId, validPipeline, newAdapter)
            .Returns(new PipelineMoveResult(validPipeline,
                AdapterRtEntityId(OctoObjectId.GenerateNewId()),
                AdapterRtEntityId(newAdapter)));
        repo.MovePipelineToAdapterAsync(TenantId, failingPipeline, newAdapter)
            .ThrowsAsync(CommunicationRepositoryException.PipelineHasNoAdapter(TenantId, failingPipeline));

        var result = await sut.MovePipelinesToAdapter(
            new MovePipelinesToAdapterRequestDto(
                new List<string>
                {
                    validPipeline.ToString(),
                    "not-an-id",
                    failingPipeline.ToString()
                },
                newAdapter.ToString(),
                false));

        var response = ((result as OkObjectResult)!.Value as MovePipelinesToAdapterResponseDto)!;
        await Assert.That(response.Results.Count).IsEqualTo(3);
        await Assert.That(response.Results[0].Success).IsTrue();
        await Assert.That(response.Results[1].Success).IsFalse();
        await Assert.That(response.Results[1].ErrorMessage!).Contains("not a valid id");
        await Assert.That(response.Results[2].Success).IsFalse();
    }
}
