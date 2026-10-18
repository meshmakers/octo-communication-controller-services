using Meshmakers.Octo.Backend.CommunicationControllerServices.Methods;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using static Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Methods.PipelineMethodTestSupport;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Methods;

/// <summary>
///     The contract between a pipeline and its method (AB#6879): what the pipeline's OutputData must look like and
///     what the caller gets for every deviation. A malformed output must never become a success.
/// </summary>
internal class PipelineMethodResultMapperTests
{
    private const string Instance = "instance-1";
    private static readonly Guid Correlation = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private static CkMethodResponse Map(PipelineMethodDefinition method, string? output,
        RtPipelineExecutionStatusEnum status = RtPipelineExecutionStatusEnum.Completed) =>
        PipelineMethodResultMapper.Map(Request(), method, Instance, status, output, Correlation, out _);

    [Test]
    public async Task ASuccessKeepsExactlyTheResultRecord()
    {
        var response = Map(Method(), "{\"value\": 42, \"debug\": \"dropped\"}");

        await Assert.That(response.Success).IsTrue();
        await Assert.That(response.ResultJson).IsEqualTo("{\"value\":42}");
        await Assert.That(response.HandlerInstanceId).IsEqualTo(Instance);
    }

    [Test]
    public async Task AMethodWithoutResultSucceedsWithoutOutput()
    {
        var response = Map(Method(resultType: null), null);

        await Assert.That(response.Success).IsTrue();
        await Assert.That(response.ResultJson).IsNull();
    }

    [Test]
    public async Task AMethodWithAResultFailsWithoutOutput()
    {
        var response = Map(Method(), null);

        await Assert.That(response.Success).IsFalse();
        await Assert.That(response.Error!.Code).IsEqualTo(CkMethodErrorCodes.MethodHandlerFailed);
        await Assert.That(response.Error.Parameters["field"]).IsEqualTo("value");
        await Assert.That(response.Error.Parameters["correlationId"]).IsEqualTo(Correlation.ToString());
    }

    [Test]
    public async Task ADeclaredErrorCarriesCodeAndParameters()
    {
        var response = Map(Method(),
            "{\"error\": {\"code\": \"FIRMWARE_LOCKED\", \"parameters\": {\"until\": \"2026-12-31\", \"attempts\": 3}}}");

        await Assert.That(response.Success).IsFalse();
        await Assert.That(response.Error!.Code).IsEqualTo("FIRMWARE_LOCKED");
        await Assert.That(response.Error.Parameters["until"]).IsEqualTo("2026-12-31");
        await Assert.That(response.Error.Parameters["attempts"]).IsEqualTo("3");
    }

    [Test]
    public async Task AnErrorCodeTheMethodDoesNotDeclareIsAHandlerFailure()
    {
        // A pipeline must not invent codes: callers render text per declared code.
        var response = Map(Method(), "{\"error\": {\"code\": \"SOMETHING_ELSE\"}}");

        await Assert.That(response.Error!.Code).IsEqualTo(CkMethodErrorCodes.MethodHandlerFailed);
        await Assert.That(response.Error.Parameters["reason"]).IsEqualTo("invalid-result");
    }

    [Test]
    [Arguments("METHOD_TARGET_NOT_FOUND")]
    [Arguments("METHOD_FORBIDDEN")]
    [Arguments("METHOD_VALIDATION_FAILED")]
    public async Task AllowedPlatformCodesAreAnsweredWithTheMethodId(string code)
    {
        var response = Map(Method(), $"{{\"error\": {{\"code\": \"{code}\"}}}}");

        await Assert.That(response.Error!.Code).IsEqualTo(code);
        await Assert.That(response.Error.Parameters["methodId"]).IsEqualTo(MethodId);
    }

    [Test]
    public async Task APipelineCannotAnswerWithAnArbitraryPlatformCode()
    {
        // METHOD_TIMEOUT / METHOD_OUTCOME_UNKNOWN are statements of the platform, not of the pipeline.
        var response = Map(Method(), "{\"error\": {\"code\": \"METHOD_TIMEOUT\"}}");

        await Assert.That(response.Error!.Code).IsEqualTo(CkMethodErrorCodes.MethodHandlerFailed);
    }

    [Test]
    [Arguments("{\"value\": \"not a number\"}")]
    [Arguments("{\"value\": 1.5}")]
    [Arguments("{\"value\": null}")]
    [Arguments("{\"other\": 1}")]
    [Arguments("[1,2,3]")]
    [Arguments("not json at all")]
    public async Task AResultThatDoesNotMatchTheResultRecordIsNeverAMalformedSuccess(string output)
    {
        var response = Map(Method(), output);

        await Assert.That(response.Success).IsFalse();
        await Assert.That(response.ResultJson).IsNull();
        await Assert.That(response.Error!.Code).IsEqualTo(CkMethodErrorCodes.MethodHandlerFailed);
        await Assert.That(response.Error.Parameters["reason"]).IsEqualTo("invalid-result");
        await Assert.That(response.Error.Parameters.ContainsKey("field")).IsTrue();
    }

    [Test]
    [Arguments(RtPipelineExecutionStatusEnum.Failed)]
    [Arguments(RtPipelineExecutionStatusEnum.Interrupted)]
    [Arguments(RtPipelineExecutionStatusEnum.Cancelled)]
    public async Task APipelineThatDidNotCompleteIsAHandlerFailureWithoutTheOutput(RtPipelineExecutionStatusEnum status)
    {
        var response = Map(Method(), "{\"value\": 1}", status);

        await Assert.That(response.Success).IsFalse();
        await Assert.That(response.Error!.Code).IsEqualTo(CkMethodErrorCodes.MethodHandlerFailed);
        await Assert.That(response.Error.Parameters["correlationId"]).IsEqualTo(Correlation.ToString());
        await Assert.That(response.ResultJson).IsNull();
    }

    [Test]
    [Arguments(RtPipelineExecutionStatusEnum.Running, false)]
    [Arguments(RtPipelineExecutionStatusEnum.Queued, false)]
    [Arguments(RtPipelineExecutionStatusEnum.Completed, true)]
    [Arguments(RtPipelineExecutionStatusEnum.Failed, true)]
    public async Task OnlyAFinalStatusEndsTheWait(RtPipelineExecutionStatusEnum status, bool terminal)
    {
        await Assert.That(PipelineMethodResultMapper.IsTerminal(status)).IsEqualTo(terminal);
    }

    [Test]
    [Arguments(AttributeValueTypesDto.String, "\"text\"", true)]
    [Arguments(AttributeValueTypesDto.String, "5", false)]
    [Arguments(AttributeValueTypesDto.Boolean, "true", true)]
    [Arguments(AttributeValueTypesDto.Boolean, "\"true\"", false)]
    [Arguments(AttributeValueTypesDto.DateTime, "\"2026-10-18T10:00:00Z\"", true)]
    [Arguments(AttributeValueTypesDto.DateTime, "\"yesterday\"", false)]
    [Arguments(AttributeValueTypesDto.Double, "1.5", true)]
    [Arguments(AttributeValueTypesDto.Record, "{\"a\":1}", true)]
    [Arguments(AttributeValueTypesDto.Record, "[]", false)]
    [Arguments(AttributeValueTypesDto.StringArray, "[\"a\",\"b\"]", true)]
    [Arguments(AttributeValueTypesDto.StringArray, "[\"a\",1]", false)]
    [Arguments(AttributeValueTypesDto.Enum, "\"Active\"", true)]
    public async Task TheValueKindIsCheckedAgainstTheResultType(AttributeValueTypesDto type, string json, bool fits)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);

        await Assert.That(PipelineMethodResultMapper.ValueMatches(document.RootElement,
            new CkMethodResultDto { ValueType = type })).IsEqualTo(fits);
    }
}
