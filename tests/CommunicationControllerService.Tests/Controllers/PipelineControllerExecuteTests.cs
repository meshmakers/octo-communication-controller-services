using System.Text;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Backend.CommunicationControllerServices.TenantApi.v1.Controllers;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Contracts.MessageObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Controllers;

/// <summary>
///     AB#7460 (Feature AB#6884, decision note AB#7120): the REST execute endpoint can never put a method-invocation
///     marker on the execution. It binds only the query <c>pipelineRtId</c> and <c>isDryRun</c> plus the raw body as the
///     pipeline input, so even a body that looks like a marker is just input data.
/// </summary>
internal class PipelineControllerExecuteTests
{
    private const string TenantId = "acme";

    [Test]
    public async Task ExecutePipeline_BodyWithMethodInvocationMember_PassesNoMarkerAndKeepsTheBodyAsInput()
    {
        var triggers = Substitute.For<ITriggerManagementService>();
        triggers.StartExecutePipelineAsync(Arg.Any<string>(), Arg.Any<OctoObjectId>(), Arg.Any<string?>(),
                Arg.Any<bool>(), Arg.Any<ExecutePipelineCaller?>(), Arg.Any<string?>(),
                Arg.Any<RtPipelineTriggerTypeEnum>(), Arg.Any<ExecutePipelineMethodInvocation?>())
            .Returns(new PipelineExecutionDataDto { Id = Guid.NewGuid(), DateTime = DateTime.UtcNow });
        var sut = new PipelineController(NullLogger<PipelineController>.Instance, triggers,
            Substitute.For<IAdapterService>(), Substitute.For<ICommunicationRepository>(),
            Substitute.For<ICommunicationEventService>());
        var httpContext = new DefaultHttpContext();
        httpContext.Request.RouteValues["tenantId"] = TenantId;
        const string body =
            "{\"methodInvocation\":{\"methodId\":\"Acme.Assets/Device.Create-1\",\"invocationId\":\"b0b0b0b0-0000-0000-0000-000000000001\"," +
            "\"ckTypeId\":\"Acme.Assets/Device\",\"pipelineRtId\":\"000000000000000000000001\"}}";
        httpContext.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        sut.ControllerContext = new ControllerContext { HttpContext = httpContext };
        var pipelineRtId = OctoObjectId.GenerateNewId();

        var result = await sut.ExecutePipeline(pipelineRtId);

        await Assert.That(result is OkObjectResult).IsTrue();
        var call = triggers.ReceivedCalls().Single();
        var args = call.GetArguments();
        await Assert.That(args[2]).IsEqualTo(body);
        // methodInvocation is the last parameter of StartExecutePipelineAsync
        await Assert.That(args[^1]).IsNull();
    }
}
