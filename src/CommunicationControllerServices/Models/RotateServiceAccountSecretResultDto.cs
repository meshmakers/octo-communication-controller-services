namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Models;

/// <summary>
/// Answer of <c>POST {tenantId}/v1/adapter/{adapterRtId}/serviceAccount/rotateSecret</c> (AB#5032).
/// </summary>
/// <remarks>
/// 🔴 It deliberately carries <b>no secret</b>. The plaintext lives in exactly two places — the
/// tenant's <c>ServiceAccountConfiguration</c> entity and the identity client's hash — and returning
/// it over the API would add a third that ends up in proxy logs, shell history and CI output.
/// </remarks>
public class RotateServiceAccountSecretResultDto
{
    /// <summary>The identity client whose secret was replaced.</summary>
    public string ClientId { get; set; } = null!;

    /// <summary>
    /// <c>RtWellKnownName</c> of the configuration entity holding the new secret — the key the mesh
    /// adapter resolves its execution identity by.
    /// </summary>
    public string ConfigurationWellKnownName { get; set; } = null!;

    /// <summary>
    /// <c>true</c> when the adapter had no service account yet and the call provisioned one instead
    /// of rotating. Nothing was invalidated in that case.
    /// </summary>
    public bool WasCreated { get; set; }

    /// <summary>
    /// <c>true</c> when the adapter's pipelines / data flows must be redeployed before the new
    /// secret takes effect — the adapter caches the credentials in the pipeline's
    /// <c>GlobalConfiguration</c> at registration time and never refreshes them.
    /// </summary>
    public bool RequiresPipelineRedeploy { get; set; }

    /// <summary>Operator-facing summary, including the redeploy instruction when one is needed.</summary>
    public string Message { get; set; } = null!;
}
