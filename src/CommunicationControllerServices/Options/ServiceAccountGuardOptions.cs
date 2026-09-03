namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Options;

/// <summary>
///     Rollout switches for the hardened service-account deploy guard (AB#5112, Epic AB#4979).
/// </summary>
/// <remarks>
///     AB#5027 made a <b>resolvable</b> service account a deploy precondition; AB#5112 additionally
///     verifies that the resolved configuration is <b>usable</b>: a non-empty client secret
///     (checked unconditionally — it is a local fact of the tenant entity and costs nothing), and
///     an existing identity client (checked against the identity service, gated here). The
///     defaults enforce both, because the AB#5111 reconcile that heals every violation ships in
///     the same train — but an operator rolling the controller ahead of a tenant sweep, or hitting
///     an identity-side regression, can loosen the client check per environment
///     (<c>OCTO_SERVICEACCOUNTGUARD__CHECKIDENTITYCLIENT=false</c>) without a release.
///     <para>
///         An <b>unreachable</b> identity service never blocks a deploy regardless of this option:
///         the lookup failure is logged as a warning and the deploy proceeds — identity downtime
///         must not brick pipeline deploys (see
///         <c>AdapterService.EnsurePipelineHasServiceAccountAsync</c>).
///     </para>
/// </remarks>
internal class ServiceAccountGuardOptions
{
    /// <summary>
    ///     Configuration section, i.e. <c>OCTO_SERVICEACCOUNTGUARD__CHECKIDENTITYCLIENT</c>.
    /// </summary>
    public const string SectionName = "ServiceAccountGuard";

    /// <summary>
    ///     Whether the deploy guard verifies that the resolved service account's identity client
    ///     actually exists in the tenant. Default <c>true</c> (enforced); set to <c>false</c> to
    ///     fall back to the AB#5027 behaviour (resolution + secret only).
    /// </summary>
    public bool CheckIdentityClient { get; set; } = true;
}
