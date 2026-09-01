namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Options;

/// <summary>
///     How <c>/operatorHub</c> treats a connection that does not satisfy
///     <see cref="Constants.SystemCommunicationApiPolicy" />.
/// </summary>
internal enum OperatorHubAuthorizationMode
{
    /// <summary>
    ///     Connection outcomes identical to a controller with no gate at all, but every connection an
    ///     enforcing run would refuse is logged as a warning naming the caller. <b>The default</b>, and
    ///     the mode the whole operator fleet needs until the SDK sends a real token — see the remarks
    ///     on <see cref="OperatorHubAuthorizationOptions" />.
    /// </summary>
    LogOnly = 0,

    /// <summary>
    ///     A connection that does not satisfy the policy is refused with a <c>HubException</c>.
    /// </summary>
    Enforce = 1
}

/// <summary>
///     Staged authorization of the operator control plane at <c>/operatorHub</c> (AB#5059).
/// </summary>
/// <remarks>
///     🔴 <b>Why this is staged instead of a plain <c>[Authorize]</c> on the hub.</b> The hub carries no
///     authorization at all today: it has no <c>[Authorize]</c> attribute, the service registers no
///     <c>FallbackPolicy</c>, and <c>app.MapHub&lt;OperatorHub&gt;("/operatorHub")</c> has no
///     <c>RequireAuthorization()</c>. Anything that can reach the endpoint can call
///     <c>RegisterOperatorAsync</c>, claim pools of any tenant, and report workload deploy / scale
///     outcomes back into the state machine — the tenant-crossing control plane of the whole adapter
///     estate.
///     <para>
///         It cannot simply be closed, because <b>the operator fleet sends no usable token</b>. The
///         connection is built by <c>SignalRClient.CreateHubConnection</c> in <c>octo-sdk</c>
///         (<c>Sdk.ServiceClient/SignalRClient.cs</c>), which contains a literal
///         <c>options.Headers["Authorization"] = "Bearer your-access-token"</c> under a
///         <c>// TODO: Handle authentication</c>, and
///         <c>OperatorHubClientFactory</c> (octo-communication-operator) hands the client a freshly
///         constructed, never-populated <c>ServiceClientAccessToken</c>. The operator's
///         <c>OperatorOptions</c> has no client id, secret or authority to obtain one from either.
///         A <c>[Authorize]</c> here would therefore 401 every operator in the estate at negotiate —
///         central and edge alike — and every pool would go Unregistered with no workload deploys.
///         Giving the operator a credential is a precondition that lives in <c>octo-sdk</c> and
///         <c>octo-communication-operator</c>, outside the reach of a gate on this side.
///     </para>
///     <para>
///         So the gate ships in <see cref="OperatorHubAuthorizationMode.LogOnly" />: outcomes are
///         unchanged, and every connection an enforcing run would refuse is logged with the caller's
///         identity. That log is the consumer inventory — the same staging shape
///         <c>TenantAuthorizationOptions</c> uses for the transport tenant gate (AB#5032 / AB#5054),
///         deliberately, so an operator has one mental model for both. Once the operator authenticates,
///         set <c>OCTO_OPERATORHUBAUTHORIZATION__MODE=Enforce</c> per environment; no code change and no
///         release is needed to arm it.
///     </para>
///     <para>
///         There is no third "off" value: <see cref="OperatorHubAuthorizationMode.LogOnly" /> already
///         changes no outcome, so a silent mode would only buy the ability to hide the inventory.
///     </para>
/// </remarks>
internal class OperatorHubAuthorizationOptions
{
    /// <summary>
    ///     Configuration section, i.e. <c>OCTO_OPERATORHUBAUTHORIZATION__MODE</c>.
    /// </summary>
    public const string SectionName = "OperatorHubAuthorization";

    /// <summary>
    ///     Enforcement mode. Defaults to <see cref="OperatorHubAuthorizationMode.LogOnly" />, which is
    ///     also the enum's zero value, so an unbound section keeps today's behaviour.
    /// </summary>
    public OperatorHubAuthorizationMode Mode { get; set; } = OperatorHubAuthorizationMode.LogOnly;
}
