using System.Text.Json;
using System.Text.Json.Serialization;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Secrets;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;

/// <summary>
///     Seals exactly as the controller did before AB#9127: the same wrapper, encrypted with the plain
///     <see cref="ISecretAttributeProtector.Protect(string)" /> (no purpose in the associated data), i.e. the call that
///     encrypts every Secret attribute. Produces the values that executions queued before the upgrade carry.
/// </summary>
internal sealed class FormerFormExecutionEnvelope(ISecretAttributeProtector protector) : IExecutionEnvelope
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = null };

    public string? Seal<T>(string purpose, T payload, string tenantId, OctoObjectId executionRtId)
    {
        var wrapper = new Wrapper
        {
            Version = 1,
            Purpose = purpose,
            TenantId = tenantId,
            ExecutionRtId = executionRtId.ToString(),
            Data = JsonSerializer.SerializeToElement(payload, JsonOptions)
        };
        return protector.Protect(JsonSerializer.Serialize(wrapper, JsonOptions)).Envelope;
    }

    public bool TryOpen<T>(string purpose, string? stored, string tenantId, OctoObjectId executionRtId,
        out T? payload, out ExecutionEnvelopeRejection rejection)
    {
        throw new NotSupportedException("The former form is only produced here, never opened.");
    }

    public string? Reseal<T>(string purpose, string? stored, string tenantId, OctoObjectId fromExecutionRtId,
        OctoObjectId toExecutionRtId)
    {
        throw new NotSupportedException("The former form is only produced here, never re-sealed.");
    }

    private sealed class Wrapper
    {
        [JsonPropertyName("v")] public int Version { get; set; }
        [JsonPropertyName("p")] public string? Purpose { get; set; }
        [JsonPropertyName("t")] public string? TenantId { get; set; }
        [JsonPropertyName("e")] public string? ExecutionRtId { get; set; }
        [JsonPropertyName("d")] public JsonElement Data { get; set; }
    }
}
