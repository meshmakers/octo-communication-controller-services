using System.Diagnostics.Metrics;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NLog;
using OpenTelemetry.Metrics;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Hubs;

/// <summary>
///     Which hub gate made a decision. Rendered as the <c>octo.hub.name</c> attribute.
/// </summary>
internal enum HubAuthorizationHub
{
    /// <summary><c>/{tenantId}/adapterHub</c>, gated by <see cref="AdapterHubAuthorizationFilter" /> (AB#5063).</summary>
    Adapter,

    /// <summary><c>/operatorHub</c>, gated by <see cref="OperatorHubAuthorizationFilter" /> (AB#5059).</summary>
    Operator,

    /// <summary>
    ///     <c>/adapterPoolHub</c>, gated by <see cref="AdapterPoolHubAuthorizationFilter" /> at connect
    ///     and completed by <c>AdapterPoolHub.RegisterPoolMemberAsync</c> at registration (AB#6088).
    ///     Rendered as <c>adapterPoolHub</c>.
    /// </summary>
    AdapterPool
}

/// <summary>
///     What a hub gate did with one connection. Rendered as the <c>octo.hub.authorization.outcome</c>
///     attribute.
/// </summary>
internal enum HubAuthorizationOutcome
{
    /// <summary>The connection satisfied every check of its gate.</summary>
    Allowed,

    /// <summary>
    ///     The connection failed a check and was let through anyway because the gate runs in
    ///     <c>LogOnly</c>. This is the number that has to be zero before <c>Enforce</c> is armed.
    /// </summary>
    WouldRefuse,

    /// <summary>The connection failed a check and was refused because the gate runs in <c>Enforce</c>.</summary>
    Refused
}

/// <summary>
///     Why a hub gate decided the way it did. Rendered as the <c>octo.hub.authorization.reason</c>
///     attribute. A closed set on purpose: the attribute must stay low-cardinality, so the free-text
///     detail (caller, token tenant, route tenant) stays in the warning log line and never reaches
///     a metric label.
/// </summary>
internal enum HubAuthorizationReason
{
    /// <summary>Allowed: the caller satisfied the policy (and, on the adapter hub, the tenant binding).</summary>
    Authorized,

    /// <summary>
    ///     Allowed on the adapter hub because the client is listed in
    ///     <c>TenantAuthorizationOptions.CrossTenantServiceClientIds</c>. Reported separately so the
    ///     use of the escape hatch stays visible.
    /// </summary>
    CrossTenantClient,

    /// <summary>No token, or a token the bearer scheme did not accept.</summary>
    Unauthenticated,

    /// <summary>A valid token without the scope the gate's policy requires (<c>octo_api</c>).</summary>
    MissingScope,

    /// <summary>Adapter hub only: the connection's path carries no tenant.</summary>
    NoRouteTenant,

    /// <summary>Adapter and pool hub: the token carries no <c>tenant_id</c> claim (fail closed).</summary>
    NoTenantClaim,

    /// <summary>
    ///     Adapter hub: the token's tenant is not the tenant of the hub path. Pool hub: the token's
    ///     tenant is not the tenant of the pool the member declares at registration.
    /// </summary>
    TenantMismatch
}

/// <summary>
///     The decision counter of the three hub gates (AB#5059 / AB#5063 / AB#6088 rollout to <c>Enforce</c>,
///     AB#5528 phase 3): <c>octo.communication.hub.authorization.decisions</c>.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why a metric next to the warning log.</b> The <c>LogOnly</c> warning line is the consumer
///         inventory — it names the caller and the tenants, which is what somebody has to act on. But
///         "has the inventory been empty on this cluster for three days?" is a question about a rate,
///         and a rate over log lines is fragile (sampling, spam filters, retention). The counter is the
///         go / no-go signal for arming <c>Enforce</c>; the log line says whom to fix when it is not
///         zero. Every decision is counted, <i>allowed</i> included, so the share of anonymous
///         connections is readable and a gate that silently stopped running (counter flat at zero)
///         is distinguishable from a clean one.
///     </para>
///     <para>
///         <b>No tenant attribute, and therefore no per-tenant opt-in gate.</b> The workload and
///         pipeline instruments on this meter carry <c>octo.tenant.id</c> and are governed by
///         <see cref="WorkloadObservabilityOptIn" /> (AB#5432). These are not: they describe the
///         security posture of the installation, a tenant cannot opt out of being inventoried, and
///         the operator hub is not tenant-addressed at all. Keeping the tenant out keeps the series
///         count fixed (hub × mode × outcome × reason, a few dozen at most) regardless of how many
///         tenants or adapters exist; the tenant of an offending adapter is in the log line.
///     </para>
///     <para>
///         Instance-based through <see cref="IMeterFactory" /> rather than static like
///         <c>WorkloadLifecycleMetrics</c>: the meter name is the same
///         (<see cref="MeterName" />, registered in octo-common-services' <c>ObservabilityBuilder</c>),
///         so the exporter sees no difference, but a test gets its own meter per service provider and
///         can assert exact counts without a per-test tenant to filter on.
///     </para>
/// </remarks>
internal sealed class HubAuthorizationMetrics
{
    /// <summary>Meter name; the one every instrument of this service publishes under.</summary>
    public const string MeterName = WorkloadLifecycleMetrics.MeterName;

    /// <summary>Instrument name. In PromQL: <c>{otel_metric_name="octo.communication.hub.authorization.decisions"}</c>.</summary>
    public const string InstrumentName = "octo.communication.hub.authorization.decisions";

    public const string HubTag = "octo.hub.name";
    public const string ModeTag = "octo.hub.authorization.mode";
    public const string OutcomeTag = "octo.hub.authorization.outcome";
    public const string ReasonTag = "octo.hub.authorization.reason";

    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private readonly Counter<long> _decisions;

    public HubAuthorizationMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName, "1.0.0");
        _decisions = meter.CreateCounter<long>(
            InstrumentName,
            unit: "{decision}",
            description:
            "Hub connection authorization decisions of the communication controller, by hub " +
            "(adapter / operator / adapterPoolHub), mode (log_only / enforce), outcome (allowed / would_refuse / refused) " +
            "and reason. would_refuse must be zero before a hub is switched to Enforce");

    }

    /// <summary>
    ///     Emits the zero baseline for every refusal series (and the allowed ones), 30 series in total.
    /// </summary>
    /// <remarks>
    ///     A cumulative counter series only exists from its first measurement on, and PromQL's
    ///     <c>increase()</c> does not count that first point - so a single anonymous connection right after
    ///     a controller restart would otherwise read as "no increase" and make a cluster look ready for
    ///     Enforce. <b>It must run after the OpenTelemetry <c>MeterProvider</c> listens</b>: a measurement
    ///     without a listener is dropped, and the provider is only built when the host starts (AB#6740).
    ///     <see cref="HubAuthorizationMetricsBaselineService" /> does that; never call this from the constructor
    ///     or from code that runs between <c>builder.Build()</c> and the host start.
    /// </remarks>
    public void EmitBaseline()
    {
        foreach (var (hub, reasons) in BaselineReasons)
        {
            foreach (var enforcing in new[] { false, true })
            {
                foreach (var reason in reasons)
                {
                    var outcome = reason is HubAuthorizationReason.Authorized or HubAuthorizationReason.CrossTenantClient
                        ? HubAuthorizationOutcome.Allowed
                        : RefusalOutcome(enforcing);
                    _decisions.Add(0, Tags(hub, enforcing, outcome, reason));
                }
            }
        }
    }

    private static readonly (HubAuthorizationHub Hub, HubAuthorizationReason[] Reasons)[] BaselineReasons =
    [
        (HubAuthorizationHub.Adapter, Enum.GetValues<HubAuthorizationReason>()),
        (HubAuthorizationHub.Operator,
            [HubAuthorizationReason.Authorized, HubAuthorizationReason.Unauthenticated, HubAuthorizationReason.MissingScope]),
        (HubAuthorizationHub.AdapterPool,
        [
            HubAuthorizationReason.Authorized, HubAuthorizationReason.Unauthenticated,
            HubAuthorizationReason.MissingScope, HubAuthorizationReason.NoTenantClaim,
            HubAuthorizationReason.TenantMismatch
        ])
    ];

    /// <summary>
    ///     Counts one decision. <paramref name="enforcing" /> is the gate's mode at the time of the
    ///     decision, so a cluster's mode is visible on the series itself.
    /// </summary>
    public void Record(HubAuthorizationHub hub, bool enforcing, HubAuthorizationOutcome outcome,
        HubAuthorizationReason reason)
    {
        try
        {
            _decisions.Add(1, Tags(hub, enforcing, outcome, reason));
        }
        catch (Exception e)
        {
            // A listener callback that throws must cost the measurement, never the connection.
            Logger.Debug(e, "Could not record a hub authorization decision");
        }
    }

    private static KeyValuePair<string, object?>[] Tags(HubAuthorizationHub hub, bool enforcing,
        HubAuthorizationOutcome outcome, HubAuthorizationReason reason) =>
    [
        new(HubTag, ToTagValue(hub)),
        new(ModeTag, enforcing ? "enforce" : "log_only"),
        new(OutcomeTag, ToTagValue(outcome)),
        new(ReasonTag, ToTagValue(reason))
    ];

    /// <summary>
    ///     The registered instance, or <c>null</c> when it is missing or cannot be constructed - the
    ///     hub filters must never fail a connection over a metric.
    /// </summary>
    public static HubAuthorizationMetrics? TryResolve(IServiceProvider serviceProvider)
    {
        try
        {
            return serviceProvider.GetService<HubAuthorizationMetrics>();
        }
        catch (Exception e)
        {
            Logger.Debug(e, "Could not resolve the hub authorization metrics");
            return null;
        }
    }

    /// <summary>
    ///     The outcome of a failed check under the given mode.
    /// </summary>
    public static HubAuthorizationOutcome RefusalOutcome(bool enforcing) =>
        enforcing ? HubAuthorizationOutcome.Refused : HubAuthorizationOutcome.WouldRefuse;

    internal static string ToTagValue(HubAuthorizationHub hub) => hub switch
    {
        HubAuthorizationHub.Adapter => "adapter",
        HubAuthorizationHub.Operator => "operator",
        HubAuthorizationHub.AdapterPool => "adapterPoolHub",
        _ => throw new ArgumentOutOfRangeException(nameof(hub), hub, null)
    };

    internal static string ToTagValue(HubAuthorizationOutcome outcome) => outcome switch
    {
        HubAuthorizationOutcome.Allowed => "allowed",
        HubAuthorizationOutcome.WouldRefuse => "would_refuse",
        HubAuthorizationOutcome.Refused => "refused",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null)
    };

    internal static string ToTagValue(HubAuthorizationReason reason) => reason switch
    {
        HubAuthorizationReason.Authorized => "authorized",
        HubAuthorizationReason.CrossTenantClient => "cross_tenant_client",
        HubAuthorizationReason.Unauthenticated => "unauthenticated",
        HubAuthorizationReason.MissingScope => "missing_scope",
        HubAuthorizationReason.NoRouteTenant => "no_route_tenant",
        HubAuthorizationReason.NoTenantClaim => "no_tenant_claim",
        HubAuthorizationReason.TenantMismatch => "tenant_mismatch",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, null)
    };
}


internal static class HubAuthorizationMetricsServiceCollectionExtensions
{
    public static IServiceCollection AddHubAuthorizationMetrics(this IServiceCollection services)
    {
        services.AddSingleton<HubAuthorizationMetrics>();
        services.AddHostedService<HubAuthorizationMetricsBaselineService>();
        return services;
    }
}

/// <summary>
///     Emits the zero baseline of <see cref="HubAuthorizationMetrics" /> once the OpenTelemetry
///     <c>MeterProvider</c> exists (AB#6740). Resolving <see cref="MeterProvider" /> builds it - and attaches its
///     listener - if the OpenTelemetry hosted service did not do so yet, so the result does not depend on the
///     registration order of hosted services. Without OpenTelemetry (tests, tools) there is nothing to resolve and
///     the baseline is emitted anyway, which is harmless.
/// </summary>
internal sealed class HubAuthorizationMetricsBaselineService(IServiceProvider serviceProvider) : IHostedService
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            _ = serviceProvider.GetService<MeterProvider>();
            serviceProvider.GetRequiredService<HubAuthorizationMetrics>().EmitBaseline();
        }
        catch (Exception e)
        {
            // A metric must never keep the controller from starting.
            Logger.Warn(e, "Could not emit the hub authorization zero baseline");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
