namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Options;

/// <summary>
///     How <c>/{tenantId}/adapterHub</c> treats a connection that does not satisfy
///     <see cref="Constants.TenantCommunicationApiReadWritePolicy" /> or does not belong to the
///     tenant whose hub path it uses.
/// </summary>
internal enum AdapterHubAuthorizationMode
{
    /// <summary>
    ///     Connection outcomes identical to a hub with no gate at all, but every connection an
    ///     enforcing run would refuse is logged as a warning naming the caller and the two tenants.
    ///     <b>The default</b>, and the mode the whole adapter fleet needs until it sends a real token
    ///     — see the remarks on <see cref="AdapterHubAuthorizationOptions" />.
    /// </summary>
    LogOnly = 0,

    /// <summary>
    ///     A connection that fails either half of the check is refused with a <c>HubException</c>.
    /// </summary>
    Enforce = 1
}

/// <summary>
///     Staged authorization of the adapter data plane at <c>/{tenantId}/adapterHub</c> (AB#5063).
/// </summary>
/// <remarks>
///     🔴 <b>Why this is staged instead of a plain <c>[Authorize]</c> on the hub.</b> The hub carried
///     no authorization at all: no <c>[Authorize]</c> attribute, no <c>FallbackPolicy</c>, and
///     <c>app.MapHub&lt;AdapterHub&gt;("/{tenantId:tenantId}/adapterHub")</c> has no
///     <c>RequireAuthorization()</c>. Anything that could reach the endpoint could register itself as
///     an adapter of <i>any</i> tenant, receive that tenant's pipeline configuration (which carries
///     its credentials), and write pipeline execution results, debug points and metrics into it.
///     <para>
///         <b>The tenant half is the part that has no equivalent anywhere else.</b> Unlike
///         <c>/operatorHub</c> this endpoint is tenant-addressed, and the transport gate that guards
///         the tenant-addressed REST routes — <c>TenantAuthorizationMiddleware</c>
///         (<c>UseOctoTenantAuthorization()</c>, AB#5032 / AB#5054) — never applies to it: it returns
///         early on any request without an <c>Authorization: Bearer</c> header, and a SignalR client
///         on the WebSocket / SSE transports sends its token as <c>?access_token=</c> instead. So the
///         hub's tenant binding is checked here, in the filter, and nowhere else.
///     </para>
///     <para>
///         The <b>rules</b> of that check are taken verbatim from
///         <c>TenantAuthorizationMiddleware</c> rather than invented here: exact match of the
///         <c>tenant_id</c> claim against the route tenant; no <c>tenant_id</c> means refused (fail
///         closed); a client-credentials client may cross tenants only if an operator listed it in
///         <c>TenantAuthorizationOptions.CrossTenantServiceClientIds</c>, which is why this filter
///         reads that same options object instead of growing an allow-list of its own. The
///         <b>parent-tenant administration rule (AB#5060) deliberately does not apply</b>: it is
///         limited to <i>user</i> tokens on endpoints marked
///         <c>IAllowParentTenantAdministration</c>, because a service token's <c>tenant_id</c> proves
///         much less — mirrored clients share the parent's secret and a token minted without
///         <c>acr_values</c> falls back to the system tenant, the root of the hierarchy. An adapter is
///         not a user, and a hub is not a marked endpoint.
///     </para>
///     <para>
///         🔴 It cannot simply be armed, because <b>an adapter connects anonymously</b>. The transport
///         is no longer the blocker: AB#5062 replaced the placeholder
///         <c>options.Headers["Authorization"] = "Bearer your-access-token"</c> in
///         <c>SignalRClient</c> (<c>octo-sdk</c>, <c>src/Sdk.ServiceClient/SignalRClient.cs</c>) with
///         an <c>AccessTokenProvider</c> that reads the injected <c>IServiceClientAccessToken</c> on
///         every connection attempt — and returns <c>null</c> when it is blank, i.e. sends no
///         credential at all rather than a malformed one. <b>The adapter never fills it before
///         connecting.</b> <c>AdapterBuilder</c> / <c>WebAdapterBuilder</c>
///         (<c>octo-communication-sdk</c>) register the bare mutable
///         <c>ServiceClientAccessToken</c> holder, and <c>AdapterOptions</c> carries no client id,
///         secret or authority to log in with; the only writer is the mesh adapter's
///         <c>ServiceAccountTokenService</c> (AB#5027), whose only callers are two <i>pipeline
///         nodes</i> at execution time (<c>DeployPipeline@1</c>, <c>AnthropicAiQuery@1</c>). So the
///         adapter identifies itself to <c>AdapterHub</c> with the unauthenticated
///         <c>adapter-rtId</c> / <c>adapter-ckTypeId</c> headers and nothing else. The operator has
///         had an <c>OperatorAccessTokenService</c> since AB#5062; the adapter has no counterpart,
///         and that is the precondition work item for arming this — a startup client-credentials
///         login with <c>acr_values=tenant:{tenantId}</c> writing into the singleton holder
///         <i>before</i> <c>AdapterExecutionService</c> starts the hub client. Arming
///         <see cref="AdapterHubAuthorizationMode.Enforce" /> before that disconnects every adapter
///         in the estate: no pipeline deploys, no executions, no data.
///     </para>
///     <para>
///         One consequence to expect in the inventory: because the provider is re-read on every
///         reconnect, an adapter that has <i>already run</i> one of those two nodes carries a real,
///         tenant-bound <c>octo_api</c> token on its next reconnect and passes both checks. The same
///         adapter can therefore appear in the log as anonymous and later not appear at all. Absence
///         from the log is not evidence that a given adapter is authenticated.
///     </para>
///     <para>
///         So the gate ships in <see cref="AdapterHubAuthorizationMode.LogOnly" />: outcomes are
///         unchanged, and every connection an enforcing run would refuse is logged with the caller's
///         identity, the token tenant and the route tenant. That log is the consumer inventory. The
///         staging shape is deliberately the same one <c>TenantAuthorizationOptions</c> (AB#5032 /
///         AB#5054) and <see cref="OperatorHubAuthorizationOptions" /> (AB#5059) use, so an operator
///         has one mental model for all three. Once the fleet authenticates, set
///         <c>OCTO_ADAPTERHUBAUTHORIZATION__MODE=Enforce</c> per environment; no code change and no
///         release is needed to arm it.
///     </para>
///     <para>
///         There is no third "off" value: <see cref="AdapterHubAuthorizationMode.LogOnly" /> already
///         changes no outcome, so a silent mode would only buy the ability to hide the inventory.
///     </para>
/// </remarks>
internal class AdapterHubAuthorizationOptions
{
    /// <summary>
    ///     Configuration section, i.e. <c>OCTO_ADAPTERHUBAUTHORIZATION__MODE</c>.
    /// </summary>
    public const string SectionName = "AdapterHubAuthorization";

    /// <summary>
    ///     Enforcement mode. Defaults to <see cref="AdapterHubAuthorizationMode.LogOnly" />, which is
    ///     also the enum's zero value, so an unbound section keeps today's behaviour.
    /// </summary>
    public AdapterHubAuthorizationMode Mode { get; set; } = AdapterHubAuthorizationMode.LogOnly;
}
