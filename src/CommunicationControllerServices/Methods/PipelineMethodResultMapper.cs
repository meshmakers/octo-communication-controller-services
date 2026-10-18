using System.Globalization;
using System.Text.Json;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Methods;

/// <summary>
///     Maps the outcome of a pipeline execution to the method response envelope (AB#6879).
/// </summary>
/// <remarks>
///     <para>
///         The pipeline ends with <c>SetPipelineExecutionResult@1</c>; the stored <c>OutputData</c> is a JSON object
///         with one of two shapes:
///     </para>
///     <list type="bullet">
///         <item><c>{"value": ...}</c> success; <c>value</c> has the type of the method result (required when the
///             method declares a result, ignored otherwise).</item>
///         <item><c>{"error": {"code": "SOME_CODE", "parameters": {"name": "text"}}}</c> a business error; the code
///             must be declared by the method (or be one of <c>METHOD_VALIDATION_FAILED</c>,
///             <c>METHOD_TARGET_NOT_FOUND</c>, <c>METHOD_FORBIDDEN</c>).</item>
///     </list>
///     <para>
///         A pipeline that fails (execution status Failed, Interrupted, Cancelled) is answered
///         <c>METHOD_HANDLER_FAILED</c> with a correlation id; its error message is logged, never returned. Anything
///         else a pipeline writes as output is a contract violation and answered <c>METHOD_HANDLER_FAILED</c> with
///         the <c>field</c> and <c>reason</c> parameters instead of a malformed success.
///     </para>
/// </remarks>
internal static class PipelineMethodResultMapper
{
    private static readonly HashSet<string> AllowedPlatformCodes =
    [
        CkMethodErrorCodes.MethodValidationFailed,
        CkMethodErrorCodes.MethodTargetNotFound,
        CkMethodErrorCodes.MethodForbidden
    ];

    /// <summary>True when the execution status is final (the pipeline will not report another outcome).</summary>
    public static bool IsTerminal(RtPipelineExecutionStatusEnum status) =>
        status is RtPipelineExecutionStatusEnum.Completed or RtPipelineExecutionStatusEnum.Failed
            or RtPipelineExecutionStatusEnum.Interrupted or RtPipelineExecutionStatusEnum.Cancelled;

    /// <summary>
    ///     Maps a terminal execution. <paramref name="detail" /> is a log text for a pipeline contract violation (no
    ///     data of the output), <c>null</c> otherwise.
    /// </summary>
    public static CkMethodResponse Map(CkMethodRequest request, PipelineMethodDefinition method,
        string handlerInstanceId, RtPipelineExecutionStatusEnum status, string? outputData, Guid correlationId,
        out string? detail)
    {
        detail = null;
        if (status != RtPipelineExecutionStatusEnum.Completed)
        {
            detail = $"pipeline execution ended with status {status}";
            return HandlerFailed(request, handlerInstanceId, correlationId);
        }

        if (string.IsNullOrWhiteSpace(outputData))
        {
            if (!method.HasResult)
            {
                return CkMethodResponse.Succeeded(request.InvocationId, handlerInstanceId);
            }

            return InvalidResult(request, handlerInstanceId, correlationId, "value", "missing-result", out detail);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(outputData);
        }
        catch (JsonException)
        {
            // the output is never part of the message
            return InvalidResult(request, handlerInstanceId, correlationId, "$", "invalid-json", out detail);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return InvalidResult(request, handlerInstanceId, correlationId, "$", "not-an-object", out detail);
            }

            if (root.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null)
            {
                return MapError(request, method, handlerInstanceId, correlationId, error, out detail);
            }

            if (!method.HasResult)
            {
                return CkMethodResponse.Succeeded(request.InvocationId, handlerInstanceId);
            }

            if (!root.TryGetProperty("value", out var value) || value.ValueKind == JsonValueKind.Null)
            {
                return InvalidResult(request, handlerInstanceId, correlationId, "value", "missing-result", out detail);
            }

            if (!ValueMatches(value, method.Definition.Result!))
            {
                return InvalidResult(request, handlerInstanceId, correlationId, "value", "type-mismatch", out detail);
            }

            // the envelope keeps exactly the result record {"value": ...}; other members are dropped
            return CkMethodResponse.Succeeded(request.InvocationId, handlerInstanceId,
                "{\"value\":" + value.GetRawText() + "}");
        }
    }

    /// <summary>The <c>METHOD_HANDLER_FAILED</c> envelope with the correlation id.</summary>
    public static CkMethodResponse HandlerFailed(CkMethodRequest request, string handlerInstanceId, Guid correlationId) =>
        CkMethodResponse.Failed(request.InvocationId, handlerInstanceId,
            CkMethodError.Create(CkMethodErrorCodes.MethodHandlerFailed,
                (CkMethodErrorParameters.MethodId, request.MethodId),
                (CkMethodErrorParameters.CorrelationId, correlationId.ToString())));

    private static CkMethodResponse InvalidResult(CkMethodRequest request, string handlerInstanceId,
        Guid correlationId, string field, string reason, out string? detail)
    {
        detail = $"the pipeline output violates the method contract ({field}: {reason})";
        return CkMethodResponse.Failed(request.InvocationId, handlerInstanceId,
            CkMethodError.Create(CkMethodErrorCodes.MethodHandlerFailed,
                (CkMethodErrorParameters.MethodId, request.MethodId),
                (CkMethodErrorParameters.CorrelationId, correlationId.ToString()),
                (CkMethodErrorParameters.Field, field),
                (CkMethodErrorParameters.Reason, PipelineMethodReasons.InvalidResult)));
    }

    private static CkMethodResponse MapError(CkMethodRequest request, PipelineMethodDefinition method,
        string handlerInstanceId, Guid correlationId, JsonElement error, out string? detail)
    {
        if (error.ValueKind != JsonValueKind.Object ||
            !error.TryGetProperty("code", out var codeElement) || codeElement.ValueKind != JsonValueKind.String)
        {
            return InvalidResult(request, handlerInstanceId, correlationId, "error", "missing-code", out detail);
        }

        var code = codeElement.GetString()!;
        var allowed = AllowedPlatformCodes.Contains(code) ||
                      (CkMethodErrorCodes.IsValidDeclaredCode(code) && method.DeclaredErrorCodes.Contains(code));
        if (!allowed)
        {
            // a pipeline may only answer with a code its method declares; the code itself is safe to log
            return InvalidResult(request, handlerInstanceId, correlationId, "error", "undeclared-code", out detail);
        }

        var parameters = new Dictionary<string, string>();
        if (error.TryGetProperty("parameters", out var parametersElement) &&
            parametersElement.ValueKind != JsonValueKind.Null)
        {
            if (parametersElement.ValueKind != JsonValueKind.Object)
            {
                return InvalidResult(request, handlerInstanceId, correlationId, "error", "invalid-parameters",
                    out detail);
            }

            foreach (var parameter in parametersElement.EnumerateObject())
            {
                var text = parameter.Value.ValueKind switch
                {
                    JsonValueKind.String => parameter.Value.GetString(),
                    JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => parameter.Value.GetRawText(),
                    _ => null
                };
                if (text is null)
                {
                    return InvalidResult(request, handlerInstanceId, correlationId, "error", "invalid-parameters",
                        out detail);
                }

                parameters[parameter.Name] = text;
            }
        }

        detail = null;
        if (code.StartsWith(CkMethodErrorCodes.PlatformPrefix, StringComparison.Ordinal))
        {
            // platform codes carry the method id (code table of the wire contract)
            parameters[CkMethodErrorParameters.MethodId] = request.MethodId;
        }

        return CkMethodResponse.Failed(request.InvocationId, handlerInstanceId,
            new CkMethodError { Code = code, Parameters = parameters });
    }

    /// <summary>Whether a JSON value fits the declared result type of the method (shallow, by value kind).</summary>
    internal static bool ValueMatches(JsonElement value, CkMethodResultDto result) => result.ValueType switch
    {
        AttributeValueTypesDto.Integer or AttributeValueTypesDto.Integer64 =>
            value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
        AttributeValueTypesDto.Double => value.ValueKind == JsonValueKind.Number,
        AttributeValueTypesDto.String or AttributeValueTypesDto.Binary or AttributeValueTypesDto.BinaryLinked
            or AttributeValueTypesDto.Secret => value.ValueKind == JsonValueKind.String,
        AttributeValueTypesDto.Boolean => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        AttributeValueTypesDto.DateTime or AttributeValueTypesDto.DateTimeOffset =>
            value.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind,
                out _),
        AttributeValueTypesDto.TimeSpan => value.ValueKind == JsonValueKind.String &&
                                           TimeSpan.TryParse(value.GetString(), CultureInfo.InvariantCulture, out _),
        AttributeValueTypesDto.Enum => value.ValueKind is JsonValueKind.String or JsonValueKind.Number,
        AttributeValueTypesDto.Record or AttributeValueTypesDto.GeospatialPoint =>
            value.ValueKind == JsonValueKind.Object,
        AttributeValueTypesDto.StringArray => value.ValueKind == JsonValueKind.Array &&
                                              value.EnumerateArray().All(e => e.ValueKind == JsonValueKind.String),
        AttributeValueTypesDto.IntegerArray => value.ValueKind == JsonValueKind.Array &&
                                               value.EnumerateArray().All(e =>
                                                   e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out _)),
        AttributeValueTypesDto.RecordArray => value.ValueKind == JsonValueKind.Array &&
                                              value.EnumerateArray().All(e => e.ValueKind == JsonValueKind.Object),
        _ => false
    };
}
