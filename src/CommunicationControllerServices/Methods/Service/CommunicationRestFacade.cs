using System.Text.Json;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Hubs;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Meshmakers.Octo.Services.Methods;
using Meshmakers.Octo.Services.Methods.Facades;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Methods.Service;

/// <summary>
///     The REST side of the facades (AB#9048, AB#9049): maps a method envelope to the status code and the
///     <see cref="ErrorResponse" /> body the endpoint always answered, and reads the typed result. The routes, bodies and status
///     codes stay those of the REST contract; the texts of a failure are the method's (it never echoes free text of an unknown
///     exception).
/// </summary>
internal static class CommunicationRestFacade
{
    /// <summary>The declaring type of the Adapter methods.</summary>
    public const string AdapterCkTypeId = "System.Communication/Adapter";

    /// <summary>The declaring type of the DeployableWorkload methods.</summary>
    public const string DeployableWorkloadCkTypeId = "System.Communication/DeployableWorkload";

    /// <summary>The declaring type of the AdapterPool methods.</summary>
    public const string AdapterPoolCkTypeId = "System.Communication/AdapterPool";

    /// <summary>The declaring type of the Pipeline methods.</summary>
    public const string PipelineCkTypeId = "System.Communication/Pipeline";

    /// <summary>The declaring type of the DataFlow methods.</summary>
    public const string DataFlowCkTypeId = "System.Communication/DataFlow";

    /// <summary>
    ///     The bearer token of the request (without the scheme), forwarded with the method call so the handler's services can
    ///     act for the caller as on the REST path (identity lookups, delegation). Sensitive: never logged.
    /// </summary>
    public static string? BearerOf(HttpContext httpContext)
    {
        var header = httpContext.Request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        return header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? header[prefix.Length..].Trim() : null;
    }

    /// <summary>The facade of the request (registered by <c>AddCommunicationCkMethodHost</c>; a missing one fails the call).</summary>
    public static ICommunicationMethodFacade CommunicationFacade(this HttpContext httpContext) =>
        httpContext.RequestServices.GetRequiredService<ICommunicationMethodFacade>();

    /// <summary>
    ///     Error parameter of the deploy, debug and data-flow failures that names the kind of the service failure, so the facade
    ///     keeps the REST status codes (422 / 404 / 400). Additive; the declared <c>message</c> parameter stays the cause.
    /// </summary>
    public const string ReasonParameter = "reason";

    /// <summary>The adapter rejected the request (<see cref="AdapterHubCallbackException" />, REST 422).</summary>
    public const string AdapterRejected = "adapter-rejected";

    /// <summary>The adapter service refused, e.g. not deployed or not connected (<see cref="AdapterServiceException" />, REST 404).</summary>
    public const string AdapterServiceFailed = "adapter-service";

    /// <summary>Any other failure (REST 400).</summary>
    public const string Unexpected = "unexpected";

    /// <summary>The kind of a service failure for <see cref="ReasonParameter" />.</summary>
    public static string FailureReason(Exception exception) => exception switch
    {
        AdapterHubCallbackException => AdapterRejected,
        AdapterServiceException => AdapterServiceFailed,
        _ => Unexpected
    };

    /// <summary>The status code REST answered for a failure of this kind (422 / 404 / 400).</summary>
    public static int StatusOfReason(CkMethodError error) =>
        error.Parameters.TryGetValue(ReasonParameter, out var reason)
            ? reason switch
            {
                AdapterRejected => StatusCodes.Status422UnprocessableEntity,
                AdapterServiceFailed => StatusCodes.Status404NotFound,
                _ => StatusCodes.Status400BadRequest
            }
            : StatusCodes.Status400BadRequest;

    /// <summary>The audit outcome of a direct answer: <c>null</c> for a success (status below 400), else <c>REST_&lt;status&gt;</c>.</summary>
    public static string? FailureOf(IActionResult result) =>
        result is IStatusCodeActionResult { StatusCode: >= 400 and var status } ? $"REST_{status}" : null;

    /// <summary>An error answer with the REST body.</summary>
    public static ObjectResult Error(int statusCode, string message) =>
        new(new ErrorResponse { ErrorMessage = message }) { StatusCode = statusCode };

    /// <summary>The <c>message</c> parameter of a declared failure, or <c>null</c>.</summary>
    public static string? MessageOf(CkMethodError error) =>
        error.Parameters.TryGetValue(CommunicationMethodErrors.MessageParameter, out var message) ? message : null;

    /// <summary>A declared failure with the status of its <see cref="ReasonParameter" /> and its message.</summary>
    public static ObjectResult ByReason(CkMethodError error, string fallbackMessage) =>
        Error(StatusOfReason(error), MessageOf(error) ?? fallbackMessage);

    /// <summary>
    ///     A platform error (<c>METHOD_*</c>) with its documented status code (<see cref="CkMethodFacadeResults.StatusCodeFor" />):
    ///     403 for a refusal (caller tenant), 404 for a missing target (with the endpoint's text), 400 for invalid parameters,
    ///     504 for the method timeout, 503 when no handler serves it, 500 otherwise. The text names the code and the reason,
    ///     never a parameter value.
    /// </summary>
    public static ObjectResult PlatformError(CkMethodError error, string? targetNotFoundMessage = null,
        int? targetNotFoundStatus = null)
    {
        var status = error.Code == CkMethodErrorCodes.MethodTargetNotFound && targetNotFoundStatus is { } notFound
            ? notFound
            : CkMethodFacadeResults.StatusCodeFor(error.Code);
        var message = error.Code switch
        {
            CkMethodErrorCodes.MethodTargetNotFound when targetNotFoundMessage != null => targetNotFoundMessage,
            CkMethodErrorCodes.MethodValidationFailed => ValidationText(error),
            _ when error.Parameters.TryGetValue(CkMethodErrorParameters.Reason, out var reason) =>
                $"The call was refused or failed ({error.Code}, {reason}).",
            _ => $"The call was refused or failed ({error.Code})."
        };
        return Error(status, message);
    }

    /// <summary>
    ///     The REST answer of a failed envelope: <paramref name="declared" /> for an error the method declares (no <c>METHOD_</c>
    ///     prefix), <see cref="PlatformError" /> otherwise.
    /// </summary>
    public static IActionResult Failure(CkMethodResponse response, Func<CkMethodError, IActionResult> declared,
        string? targetNotFoundMessage = null, int? targetNotFoundStatus = null)
    {
        var error = response.Error ?? CkMethodError.Create(CkMethodErrorCodes.MethodHandlerFailed);
        return error.Code.StartsWith("METHOD_", StringComparison.Ordinal)
            ? PlatformError(error, targetNotFoundMessage, targetNotFoundStatus)
            : declared(error);
    }

    /// <summary>The <c>value</c> of a typed result, read with the serializer the handler wrote it with; <c>null</c> when none.</summary>
    public static TValue? ValueOf<TValue>(CkMethodResponse response) where TValue : class
    {
        if (!response.Success || string.IsNullOrEmpty(response.ResultJson))
        {
            return null;
        }

        using var document = JsonDocument.Parse(response.ResultJson);
        var root = document.RootElement;
        var value = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("value", out var wrapped) ? wrapped : root;
        return value.Deserialize<TValue>(CkMethodJson.Options);
    }

    private static string ValidationText(CkMethodError error)
    {
        // field and reason are fixed tokens of the handler (e.g. adapterRtId, invalid-object-id), never a parameter value
        var fields = new List<string>();
        if (error.Parameters.TryGetValue(CkMethodErrorParameters.Field, out var field))
        {
            fields.Add(error.Parameters.TryGetValue(CkMethodErrorParameters.Reason, out var reason)
                ? $"{field} ({reason})"
                : field);
        }

        fields.AddRange(error.Details?.Select(d =>
            d.Parameters.TryGetValue(CkMethodErrorParameters.Field, out var f) ? $"{f} ({d.Code})" : d.Code) ?? []);
        return fields.Count > 0
            ? $"The request is not valid: {string.Join(", ", fields)}."
            : "The request is not valid.";
    }
}
