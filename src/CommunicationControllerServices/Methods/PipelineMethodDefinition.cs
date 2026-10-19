using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Methods;

/// <summary>
///     A CK method declared <c>execution.handler: pipeline</c> in one tenant, with everything the pipeline handler
///     needs to know about it (AB#6879).
/// </summary>
/// <param name="TenantId">The tenant whose CK model declares the method.</param>
/// <param name="MethodId">Qualified, model-version-less method id, e.g. <c>Acme.Assets/Device.UpdateFirmware-1</c>.</param>
/// <param name="Definition">The compiled method definition from the tenant's CK cache.</param>
internal sealed record PipelineMethodDefinition(string TenantId, string MethodId, CkMethodDto Definition)
{
    /// <summary>
    ///     The well-known name of the pipeline that implements the method: the qualified method id itself. This is
    ///     the binding by convention of AB#6879 (no new CK element).
    /// </summary>
    public string PipelineWellKnownName => MethodId;

    /// <summary>True when the method is implemented by a pipeline (<c>execution.handler: pipeline</c>).</summary>
    public bool IsPipelineHandled =>
        CkMethodHandlers.Effective(Definition.Execution?.Handler) == CkMethodHandlers.Pipeline;

    /// <summary>Timeout of the method definition; a pipeline method declares it explicitly (compiler rule).</summary>
    public int TimeoutSeconds => Definition.Execution?.TimeoutSeconds ?? CkMethodExecutionDto.DefaultTimeoutSeconds;

    /// <summary>True when the method declares a result.</summary>
    public bool HasResult => Definition.Result is not null;

    /// <summary>The names of the parameters that must not be handed to a pipeline (OD-3, OD-4, OD-9 are open).</summary>
    public IReadOnlyList<string> SensitiveParameters =>
        (Definition.Parameters ?? [])
        .Where(p => p.Sensitive || p.ValueType == AttributeValueTypesDto.Secret)
        .Select(p => p.Name)
        .ToList();

    /// <summary>The error codes the method declares.</summary>
    public IReadOnlySet<string> DeclaredErrorCodes =>
        (Definition.Errors ?? []).Select(e => e.Code).ToHashSet(StringComparer.Ordinal);

    /// <summary>
    ///     Why a pipeline must not implement this method yet, or <c>null</c> when it may. Methods with sensitive
    ///     parameters are refused until the retention of pipeline input and output is decided (OD-3, OD-4, OD-9):
    ///     the pipeline execution stores its input and output.
    /// </summary>
    public string? RefusalReason => SensitiveParameters.Count > 0
        ? PipelineMethodReasons.SensitiveParametersNotSupported
        : null;
}

/// <summary>Short kebab-case <c>reason</c> tokens of the pipeline handler (wire contract, rule 1: new tokens are additive).</summary>
internal static class PipelineMethodReasons
{
    public const string SensitiveParametersNotSupported = "sensitive-parameters-not-supported";
    public const string PipelineNotDeployed = "pipeline-not-deployed";
    public const string PipelineStartFailed = "pipeline-start-failed";
    public const string MethodNotPipelineHandled = "method-not-pipeline-handled";
    public const string TenantNotEnabled = "tenant-not-enabled";
    public const string InvalidResult = "invalid-result";
    public const string InvalidParameters = "invalid-value";
    public const string MissingParameter = "missing-parameter";
}
