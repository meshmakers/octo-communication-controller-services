using Meshmakers.Octo.Backend.CommunicationControllerServices.Models;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Backend.CommunicationControllerServices.TenantApi.v1.Controllers;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Controllers;

/// <summary>
///     AB#8156 — the REST pipeline execution carries the failure kind AB#8037 stores, so an operator sees whether a
///     failed execution was an unacknowledged write (OutcomeUnknown) or an ordinary failure. Null for every other
///     execution; interrupted and offline-buffered executions carry no kind and stay null.
/// </summary>
internal class PipelineDebugControllerFailureKindTests
{
    private const string TenantId = "acme";
    private static readonly RtEntityId PipelineId = new(SystemCommunicationCkIds.RtCkPipelineTypeId, OctoObjectId.GenerateNewId());

    private static (PipelineDebugController sut, IPipelineDebugService debug, ICommunicationRepository repo) CreateSut()
    {
        var debug = Substitute.For<IPipelineDebugService>();
        var repo = Substitute.For<ICommunicationRepository>();
        var sut = new PipelineDebugController(NullLogger<PipelineDebugController>.Instance, debug, repo);
        var httpContext = new DefaultHttpContext();
        httpContext.Request.RouteValues["tenantId"] = TenantId;
        sut.ControllerContext = new ControllerContext { HttpContext = httpContext };
        debug.GetPipelineExecutionsAsync(TenantId, Arg.Any<RtEntityId>(), Arg.Any<int>(), Arg.Any<int>())
            .ThrowsAsync(PipelineDebugInformationNotFoundException.NotFound(TenantId, PipelineId));
        debug.GetLatestPipelineExecutionAsync(TenantId, Arg.Any<RtEntityId>())
            .ThrowsAsync(PipelineDebugInformationNotFoundException.NotFound(TenantId, PipelineId));
        return (sut, debug, repo);
    }

    private static RtPipelineExecution Execution(Guid id, RtPipelineExecutionStatusEnum status,
        RtPipelineExecutionFailureKindEnum? kind) =>
        new()
        {
            RtId = OctoObjectId.GenerateNewId(),
            CkTypeId = SystemCommunicationCkIds.RtCkPipelineExecutionTypeId,
            ExecutionId = id.ToString(),
            Status = status,
            FailureKind = kind,
            StartedAt = DateTime.UtcNow
        };

    private static async Task<List<PipelineExecutionDataDto>> ListAsync(PipelineDebugController sut)
    {
        var ok = await sut.GetPipelineExecutionsAsync(PipelineId) as OkObjectResult;
        await Assert.That(ok).IsNotNull();
        return ((IEnumerable<PipelineExecutionDataDto>)ok!.Value!).ToList();
    }

    [Test]
    public async Task List_FailedWithOutcomeUnknown_ShowsOutcomeUnknown()
    {
        var (sut, _, repo) = CreateSut();
        var id = Guid.NewGuid();
        repo.GetPipelineExecutionsAsync(TenantId, Arg.Any<RtEntityId>(), null, null, 0, 20)
            .Returns([Execution(id, RtPipelineExecutionStatusEnum.Failed, RtPipelineExecutionFailureKindEnum.OutcomeUnknown)]);

        var list = await ListAsync(sut);

        await Assert.That(list.Single().FailureKind).IsEqualTo(PipelineExecutionFailureKind.OutcomeUnknown);
        await Assert.That(list.Single().Status).IsEqualTo(PipelineExecutionStatus.Failed);
    }

    [Test]
    public async Task List_OnlyAFailureCarriesAKind_EverythingElseIsNull()
    {
        var (sut, _, repo) = CreateSut();
        repo.GetPipelineExecutionsAsync(TenantId, Arg.Any<RtEntityId>(), null, null, 0, 20)
            .Returns(
            [
                Execution(Guid.NewGuid(), RtPipelineExecutionStatusEnum.Failed, RtPipelineExecutionFailureKindEnum.Failed),
                // failed without a kind: adapter built before AB#8037, interrupted or offline-buffered
                Execution(Guid.NewGuid(), RtPipelineExecutionStatusEnum.Failed, null),
                Execution(Guid.NewGuid(), RtPipelineExecutionStatusEnum.Completed, null),
                // a kind on a non-failed execution is never shown
                Execution(Guid.NewGuid(), RtPipelineExecutionStatusEnum.Completed, RtPipelineExecutionFailureKindEnum.OutcomeUnknown)
            ]);

        var list = await ListAsync(sut);

        var kinds = list.Select(e => (e.Status, e.FailureKind)).OrderBy(t => t.Status).ToList();
        await Assert.That(list.Count(e => e.FailureKind == PipelineExecutionFailureKind.Failed)).IsEqualTo(1);
        await Assert.That(list.Count(e => e.FailureKind is null)).IsEqualTo(3);
        await Assert.That(kinds.Any(t => t.Status == PipelineExecutionStatus.Completed && t.FailureKind is not null)).IsFalse();
    }

    [Test]
    public async Task List_DebugCachedExecution_KeepsTheKindOfItsPersistedTwin()
    {
        var (sut, debug, repo) = CreateSut();
        var id = Guid.NewGuid();
        debug.GetPipelineExecutionsAsync(TenantId, Arg.Any<RtEntityId>(), Arg.Any<int>(), Arg.Any<int>())
            .Returns(Task.FromResult<IEnumerable<PipelineExecutionDataDto>>(
                [new PipelineExecutionDataDto { Id = id, DateTime = DateTime.UtcNow, HasDebugData = true }]));
        repo.GetPipelineExecutionsAsync(TenantId, Arg.Any<RtEntityId>(), null, null, 0, 20)
            .Returns([Execution(id, RtPipelineExecutionStatusEnum.Failed, RtPipelineExecutionFailureKindEnum.OutcomeUnknown)]);

        var list = await ListAsync(sut);

        await Assert.That(list.Count).IsEqualTo(1);
        await Assert.That(list[0].HasDebugData).IsTrue();
        await Assert.That(list[0].FailureKind).IsEqualTo(PipelineExecutionFailureKind.OutcomeUnknown);
    }

    [Test]
    public async Task Latest_FromTheDatabase_ShowsTheKind()
    {
        var (sut, _, repo) = CreateSut();
        repo.GetPipelineExecutionsAsync(TenantId, Arg.Any<RtEntityId>(), null, null, 0, 1)
            .Returns([Execution(Guid.NewGuid(), RtPipelineExecutionStatusEnum.Failed, RtPipelineExecutionFailureKindEnum.OutcomeUnknown)]);

        var ok = await sut.GetLatestPipelineExecutionAsync(PipelineId) as OkObjectResult;

        await Assert.That(ok).IsNotNull();
        await Assert.That(((PipelineExecutionDataDto)ok!.Value!).FailureKind).IsEqualTo(PipelineExecutionFailureKind.OutcomeUnknown);
    }

    [Test]
    public async Task Latest_FromTheDebugCache_LooksTheKindUpOnThePersistedExecution()
    {
        var (sut, debug, repo) = CreateSut();
        var id = Guid.NewGuid();
        debug.GetLatestPipelineExecutionAsync(TenantId, Arg.Any<RtEntityId>())
            .Returns(Task.FromResult(new PipelineExecutionDataDto { Id = id, DateTime = DateTime.UtcNow, HasDebugData = true }));
        repo.GetPipelineExecutionAsync(TenantId, id.ToString())
            .Returns(Execution(id, RtPipelineExecutionStatusEnum.Failed, RtPipelineExecutionFailureKindEnum.OutcomeUnknown));

        var ok = await sut.GetLatestPipelineExecutionAsync(PipelineId) as OkObjectResult;

        var dto = (PipelineExecutionDataDto)ok!.Value!;
        await Assert.That(dto.HasDebugData).IsTrue();
        await Assert.That(dto.FailureKind).IsEqualTo(PipelineExecutionFailureKind.OutcomeUnknown);
    }

    [Test]
    public async Task Latest_FromTheDebugCache_WhenTheLookupFails_StillAnswersWithoutAKind()
    {
        var (sut, debug, repo) = CreateSut();
        var id = Guid.NewGuid();
        debug.GetLatestPipelineExecutionAsync(TenantId, Arg.Any<RtEntityId>())
            .Returns(Task.FromResult(new PipelineExecutionDataDto { Id = id, DateTime = DateTime.UtcNow, HasDebugData = true }));
        repo.GetPipelineExecutionAsync(TenantId, id.ToString()).ThrowsAsync(new InvalidOperationException("db down"));

        var ok = await sut.GetLatestPipelineExecutionAsync(PipelineId) as OkObjectResult;

        await Assert.That(((PipelineExecutionDataDto)ok!.Value!).FailureKind).IsNull();
    }

    [Test]
    [Arguments(RtPipelineExecutionStatusEnum.Failed, RtPipelineExecutionFailureKindEnum.OutcomeUnknown, PipelineExecutionFailureKind.OutcomeUnknown)]
    [Arguments(RtPipelineExecutionStatusEnum.Failed, RtPipelineExecutionFailureKindEnum.Failed, PipelineExecutionFailureKind.Failed)]
    public async Task FromCk_MapsAFailedExecutionsKind(RtPipelineExecutionStatusEnum status,
        RtPipelineExecutionFailureKindEnum kind, PipelineExecutionFailureKind expected)
    {
        await Assert.That(PipelineExecutionFailureKindMapping.FromCk(status, kind)).IsEqualTo(expected);
    }

    [Test]
    public async Task FromCk_IsNullWithoutAKindOrWithoutAFailure()
    {
        await Assert.That(PipelineExecutionFailureKindMapping.FromCk(RtPipelineExecutionStatusEnum.Failed, null)).IsNull();
        await Assert.That(PipelineExecutionFailureKindMapping.FromCk(RtPipelineExecutionStatusEnum.Completed,
            RtPipelineExecutionFailureKindEnum.OutcomeUnknown)).IsNull();
    }

    [Test]
    public async Task PipelineExecutionDto_CarriesTheFailureKindOfAFailedExecutionOnly()
    {
        var pipeline = PipelineId;
        var failed = PipelineExecutionDto.FromEntity(
            Execution(Guid.NewGuid(), RtPipelineExecutionStatusEnum.Failed, RtPipelineExecutionFailureKindEnum.OutcomeUnknown),
            pipeline, pipeline);
        var completed = PipelineExecutionDto.FromEntity(
            Execution(Guid.NewGuid(), RtPipelineExecutionStatusEnum.Completed, RtPipelineExecutionFailureKindEnum.OutcomeUnknown),
            pipeline, pipeline);

        await Assert.That(failed.FailureKind).IsEqualTo(RtPipelineExecutionFailureKindEnum.OutcomeUnknown);
        await Assert.That(completed.FailureKind).IsNull();
    }

    [Test]
    public async Task FailureKind_OnTheWire_IsTheEnumNameAndAbsentValuesStayNull()
    {
        var dto = new PipelineExecutionDataDto
        {
            Id = Guid.NewGuid(),
            DateTime = DateTime.UtcNow,
            Status = PipelineExecutionStatus.Failed,
            FailureKind = PipelineExecutionFailureKind.OutcomeUnknown
        };

        var json = System.Text.Json.JsonSerializer.Serialize(dto, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

        await Assert.That(json).Contains("\"failureKind\":\"OutcomeUnknown\"");
    }
}
