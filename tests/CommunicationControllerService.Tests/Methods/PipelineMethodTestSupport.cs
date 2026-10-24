using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Methods;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Methods;

/// <summary>Builders shared by the pipeline method handler tests (AB#6879).</summary>
internal static class PipelineMethodTestSupport
{
    public const string TenantId = "tenant1";
    public const string MethodId = "Acme.Assets/Device.UpdateFirmware-1";
    public const string CkTypeId = "Acme.Assets/Device";

    /// <summary>The caller token kit all handler tests share: one key, one fake identity service.</summary>
    public static readonly CallerTokenKit Kit = new();

    /// <summary>A valid user token of <see cref="TenantId" /> (subject <see cref="CallerTokenKit.TokenSubject" />).</summary>
    public static readonly string Token = Kit.Create();

    /// <summary>Default of <c>Request(token:)</c>: the valid <see cref="Token" />.</summary>
    public const string ValidToken = "<valid>";

    /// <summary>A pipeline-handled method with a boolean... integer result and one declared error.</summary>
    public static PipelineMethodDefinition Method(
        AttributeValueTypesDto? resultType = AttributeValueTypesDto.Integer,
        bool sensitiveParameter = false,
        string? handler = CkMethodHandlers.Pipeline,
        int timeoutSeconds = 5,
        string methodId = MethodId,
        bool sensitiveResult = false)
    {
        var parameters = new List<CkMethodParameterDto>
        {
            new() { Name = "version", ValueType = AttributeValueTypesDto.String },
            new() { Name = "force", ValueType = AttributeValueTypesDto.Boolean, IsOptional = true }
        };
        if (sensitiveParameter)
        {
            parameters.Add(new CkMethodParameterDto
            {
                Name = "signingKey", ValueType = AttributeValueTypesDto.String, Sensitive = true
            });
        }

        return new PipelineMethodDefinition(TenantId, methodId, new CkMethodDto
        {
            MethodId = methodId[(methodId.LastIndexOf('.') + 1)..],
            Parameters = parameters,
            Result = resultType is null ? null : new CkMethodResultDto { ValueType = resultType.Value, Sensitive = sensitiveResult },
            Errors = [new CkMethodErrorDto { Code = "FIRMWARE_LOCKED" }],
            Execution = new CkMethodExecutionDto { TimeoutSeconds = timeoutSeconds, Handler = handler }
        });
    }

    public static CkMethodRequest Request(string parametersJson = "{\"version\":\"1.2.3\"}", int deadlineSeconds = 5,
        string? token = ValidToken, Guid? invocationId = null, string methodId = MethodId) =>
        new(TenantId)
        {
            InvocationId = invocationId ?? Guid.NewGuid(),
            MethodId = methodId,
            CkTypeId = CkTypeId,
            TargetRtId = "65f0c0ffee0000000000dead",
            ParametersJson = parametersJson,
            DeadlineUtc = DateTime.UtcNow.AddSeconds(deadlineSeconds),
            CallerAccessToken = token == ValidToken ? Token : token,
            Caller = new CkMethodCaller
            {
                SubjectId = "user-42", TenantId = TenantId, Name = "Ada Admin", Roles = ["TenantAdmin"]
            }
        };
}

/// <summary>Collects everything a component logs (formatted message and exception text).</summary>
internal sealed class CapturingLogger<T> : ILogger<T>
{
    private readonly ConcurrentQueue<string> _entries = new();

    public IReadOnlyList<string> Entries => _entries.ToArray();

    public string All => string.Join("\n", _entries);

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        _entries.Enqueue($"[{logLevel}] {formatter(state, exception)}{(exception is null ? string.Empty : " | " + exception)}");
    }
}
