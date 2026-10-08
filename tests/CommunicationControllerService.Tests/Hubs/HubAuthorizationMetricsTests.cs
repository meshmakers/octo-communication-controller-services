using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using Meshmakers.Octo.Backend.CommunicationControllerServices;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Hubs;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Options;
using Meshmakers.Octo.Communication.Contracts;
using Meshmakers.Octo.Services.Infrastructure;
using Meshmakers.Octo.Services.Infrastructure.Configuration;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Connections.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Hubs;

/// <summary>
///     AB#5528 phase 3 — the decision counter of the two hub gates is the go / no-go signal for
///     arming <c>Enforce</c> per cluster: <c>would_refuse</c> has to be zero first. A wrong tag value
///     or a decision path that is not counted would make a cluster look ready while it is not, so the
///     names and values the exporter publishes are pinned through a real <see cref="MeterListener" />.
///     <para>
///         Each test gets its own service provider and therefore its own <see cref="IMeterFactory" />;
///         the listener only enables instruments of that factory, so concurrently running tests do not
///         see each other's measurements.
///     </para>
/// </summary>
internal class HubAuthorizationMetricsTests
{
    private const string RouteTenantId = "meshtest";

    private sealed class TestHub : Hub;

    private sealed class TestHttpContextFeature(HttpContext httpContext) : IHttpContextFeature
    {
        public HttpContext? HttpContext { get; set; } = httpContext;
    }

    private sealed record Decision(string Instrument, long Value, Dictionary<string, string> Tags);

    private sealed class Recorder : IDisposable
    {
        private readonly MeterListener _listener = new();

        public Recorder(IServiceProvider serviceProvider)
        {
            var factory = serviceProvider.GetRequiredService<IMeterFactory>();
            _listener.InstrumentPublished = (instrument, l) =>
            {
                // The scope isolates this test's provider; the meter name filters out the other
                // instruments the same factory serves (ASP.NET Core's authorization metrics).
                if (ReferenceEquals(instrument.Meter.Scope, factory) &&
                    instrument.Meter.Name == HubAuthorizationMetrics.MeterName)
                {
                    l.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            {
                var map = new Dictionary<string, string>();
                foreach (var tag in tags)
                {
                    map[tag.Key] = tag.Value?.ToString() ?? string.Empty;
                }

                lock (Decisions)
                {
                    if (value == 0)
                    {
                        Baseline.Add(new Decision(instrument.Name, value, map));
                    }
                    else
                    {
                        Decisions.Add(new Decision(instrument.Name, value, map));
                    }
                }
            });
            _listener.Start();
        }

        /// <summary>Real decisions (value 1).</summary>
        public List<Decision> Decisions { get; } = [];

        /// <summary>The zero baseline the constructor writes.</summary>
        public List<Decision> Baseline { get; } = [];

        public void Dispose() => _listener.Dispose();
    }

    private static ServiceProvider BuildServiceProvider(
        AdapterHubAuthorizationMode adapterMode = AdapterHubAuthorizationMode.LogOnly,
        OperatorHubAuthorizationMode operatorMode = OperatorHubAuthorizationMode.LogOnly,
        params string[] crossTenantServiceClientIds)
    {
        return new ServiceCollection()
            .AddLogging()
            .AddMetrics()
            .AddSingleton<HubAuthorizationMetrics>()
            .AddAuthorization(options =>
            {
                // Mirrors the registration in Program.cs one for one.
                options.AddPolicy(Constants.SystemCommunicationApiPolicy, policy =>
                    policy.RequireClaim(InfrastructureCommon.ClaimScope, CommonConstants.OctoApiFullAccess));
                options.AddPolicy(Constants.TenantCommunicationApiReadWritePolicy, policy =>
                    policy.RequireClaim(InfrastructureCommon.ClaimScope, CommonConstants.OctoApiFullAccess));
            })
            .Configure<AdapterHubAuthorizationOptions>(o => o.Mode = adapterMode)
            .Configure<OperatorHubAuthorizationOptions>(o => o.Mode = operatorMode)
            .Configure<TenantAuthorizationOptions>(o =>
                o.CrossTenantServiceClientIds = crossTenantServiceClientIds.ToList())
            .BuildServiceProvider();
    }

    private static HubLifetimeContext CreateContext(IServiceProvider serviceProvider, ClaimsPrincipal? user,
        string? routeTenantId = RouteTenantId)
    {
        var httpContext = new DefaultHttpContext { RequestServices = serviceProvider };
        if (routeTenantId != null)
        {
            httpContext.Request.RouteValues["tenantId"] = routeTenantId;
        }

        var features = new FeatureCollection();
        features.Set<IHttpContextFeature>(new TestHttpContextFeature(httpContext));

        var callerContext = Substitute.For<HubCallerContext>();
        callerContext.ConnectionId.Returns("conn-1");
        callerContext.User.Returns(user);
        callerContext.Features.Returns(features);

        return new HubLifetimeContext(callerContext, serviceProvider, new TestHub());
    }

    private static ClaimsPrincipal Principal(params Claim[] claims) =>
        new(new ClaimsIdentity(claims, JwtBearerDefaults.AuthenticationScheme));

    private static ClaimsPrincipal Anonymous() => new(new ClaimsIdentity());

    private static ClaimsPrincipal ServiceToken(string tenantId = RouteTenantId, string clientId = "octo-pipeline-sa",
        string scope = CommonConstants.OctoApiFullAccess) =>
        Principal(new Claim(InfrastructureCommon.ClaimScope, scope),
            new Claim("client_id", clientId),
            new Claim("tenant_id", tenantId));

    private static async Task ConnectAdapterAsync(HubLifetimeContext context)
    {
        try
        {
            await new AdapterHubAuthorizationFilter().OnConnectedAsync(context, _ => Task.CompletedTask);
        }
        catch (HubException)
        {
            // Refusals are asserted through the counter here; the filter tests pin the exception.
        }
    }

    private static async Task ConnectOperatorAsync(HubLifetimeContext context)
    {
        try
        {
            await new OperatorHubAuthorizationFilter().OnConnectedAsync(context, _ => Task.CompletedTask);
        }
        catch (HubException)
        {
            // See ConnectAdapterAsync.
        }
    }

    private static async Task AssertSingleDecision(Recorder recorder, string hub, string mode, string outcome,
        string reason)
    {
        await Assert.That(recorder.Decisions.Count).IsEqualTo(1);
        var decision = recorder.Decisions[0];
        await Assert.That(decision.Instrument).IsEqualTo("octo.communication.hub.authorization.decisions");
        await Assert.That(decision.Value).IsEqualTo(1);
        await Assert.That(decision.Tags["octo.hub.name"]).IsEqualTo(hub);
        await Assert.That(decision.Tags["octo.hub.authorization.mode"]).IsEqualTo(mode);
        await Assert.That(decision.Tags["octo.hub.authorization.outcome"]).IsEqualTo(outcome);
        await Assert.That(decision.Tags["octo.hub.authorization.reason"]).IsEqualTo(reason);
        // Low cardinality is part of the contract: exactly these four attributes, never a tenant,
        // client id or connection id.
        await Assert.That(decision.Tags.Count).IsEqualTo(4);
    }

    [Test]
    public async Task Adapter_ScopedTokenOfTheRouteTenant_IsCountedAsAllowed()
    {
        await using var provider = BuildServiceProvider(AdapterHubAuthorizationMode.Enforce);
        using var recorder = new Recorder(provider);

        await ConnectAdapterAsync(CreateContext(provider, ServiceToken()));

        await AssertSingleDecision(recorder, "adapter", "enforce", "allowed", "authorized");
    }

    [Test]
    public async Task Adapter_Anonymous_IsCountedAsWouldRefuse_InLogOnly()
    {
        await using var provider = BuildServiceProvider();
        using var recorder = new Recorder(provider);

        await ConnectAdapterAsync(CreateContext(provider, Anonymous()));

        await AssertSingleDecision(recorder, "adapter", "log_only", "would_refuse", "unauthenticated");
    }

    [Test]
    public async Task Adapter_Anonymous_IsCountedAsRefused_WhenEnforcing()
    {
        await using var provider = BuildServiceProvider(AdapterHubAuthorizationMode.Enforce);
        using var recorder = new Recorder(provider);

        await ConnectAdapterAsync(CreateContext(provider, Anonymous()));

        await AssertSingleDecision(recorder, "adapter", "enforce", "refused", "unauthenticated");
    }

    [Test]
    public async Task Adapter_ReadOnlyScope_IsCountedAsMissingScope()
    {
        await using var provider = BuildServiceProvider();
        using var recorder = new Recorder(provider);

        await ConnectAdapterAsync(CreateContext(provider, ServiceToken(scope: CommonConstants.OctoApiReadOnly)));

        await AssertSingleDecision(recorder, "adapter", "log_only", "would_refuse", "missing_scope");
    }

    [Test]
    public async Task Adapter_ForeignTenant_IsCountedAsTenantMismatch()
    {
        await using var provider = BuildServiceProvider();
        using var recorder = new Recorder(provider);

        await ConnectAdapterAsync(CreateContext(provider, ServiceToken("othertenant")));

        await AssertSingleDecision(recorder, "adapter", "log_only", "would_refuse", "tenant_mismatch");
    }

    [Test]
    public async Task Adapter_TokenWithoutTenantClaim_IsCountedAsNoTenantClaim()
    {
        await using var provider = BuildServiceProvider();
        using var recorder = new Recorder(provider);

        await ConnectAdapterAsync(CreateContext(provider,
            Principal(new Claim(InfrastructureCommon.ClaimScope, CommonConstants.OctoApiFullAccess),
                new Claim("client_id", "legacy"))));

        await AssertSingleDecision(recorder, "adapter", "log_only", "would_refuse", "no_tenant_claim");
    }

    [Test]
    public async Task Adapter_WithoutRouteTenant_IsCountedAsNoRouteTenant()
    {
        await using var provider = BuildServiceProvider();
        using var recorder = new Recorder(provider);

        await ConnectAdapterAsync(CreateContext(provider, ServiceToken(), routeTenantId: null));

        await AssertSingleDecision(recorder, "adapter", "log_only", "would_refuse", "no_route_tenant");
    }

    /// <summary>
    ///     The cross-tenant escape hatch is allowed, but reported under its own reason so its use
    ///     stays visible on the dashboard rather than disappearing into "authorized".
    /// </summary>
    [Test]
    public async Task Adapter_AllowListedCrossTenantClient_IsCountedAsAllowedWithItsOwnReason()
    {
        await using var provider = BuildServiceProvider(AdapterHubAuthorizationMode.Enforce,
            OperatorHubAuthorizationMode.LogOnly, "octo-platform-*");
        using var recorder = new Recorder(provider);

        await ConnectAdapterAsync(CreateContext(provider, ServiceToken("othertenant", "octo-platform-worker")));

        await AssertSingleDecision(recorder, "adapter", "enforce", "allowed", "cross_tenant_client");
    }

    [Test]
    public async Task Operator_ScopedToken_IsCountedAsAllowed()
    {
        await using var provider = BuildServiceProvider(operatorMode: OperatorHubAuthorizationMode.LogOnly);
        using var recorder = new Recorder(provider);

        await ConnectOperatorAsync(CreateContext(provider,
            Principal(new Claim(InfrastructureCommon.ClaimScope, CommonConstants.OctoApiFullAccess),
                new Claim("client_id", "octo-operator"))));

        await AssertSingleDecision(recorder, "operator", "log_only", "allowed", "authorized");
    }

    [Test]
    public async Task Operator_Anonymous_IsCountedAsWouldRefuse_InLogOnly()
    {
        await using var provider = BuildServiceProvider();
        using var recorder = new Recorder(provider);

        await ConnectOperatorAsync(CreateContext(provider, Anonymous()));

        await AssertSingleDecision(recorder, "operator", "log_only", "would_refuse", "unauthenticated");
    }

    [Test]
    public async Task Operator_Anonymous_IsCountedAsRefused_WhenEnforcing()
    {
        await using var provider = BuildServiceProvider(operatorMode: OperatorHubAuthorizationMode.Enforce);
        using var recorder = new Recorder(provider);

        await ConnectOperatorAsync(CreateContext(provider, Anonymous()));

        await AssertSingleDecision(recorder, "operator", "enforce", "refused", "unauthenticated");
    }

    [Test]
    public async Task Operator_TokenWithoutTheSystemScope_IsCountedAsMissingScope()
    {
        await using var provider = BuildServiceProvider(operatorMode: OperatorHubAuthorizationMode.Enforce);
        using var recorder = new Recorder(provider);

        await ConnectOperatorAsync(CreateContext(provider,
            Principal(new Claim(InfrastructureCommon.ClaimScope, CommonConstants.OctoApiReadOnly),
                new Claim("client_id", "octo-operator"))));

        await AssertSingleDecision(recorder, "operator", "enforce", "refused", "missing_scope");
    }

    /// <summary>
    ///     🔴 A missing metrics registration must cost the counter, never the connection — the filters
    ///     resolve the metrics with <c>GetService</c>. The provider here has no
    ///     <see cref="HubAuthorizationMetrics" />; a LogOnly gate must still let the anonymous caller in.
    /// </summary>
    [Test]
    public async Task MissingMetricsRegistration_DoesNotAffectTheConnection()
    {
        await using var provider = new ServiceCollection()
            .AddLogging()
            .AddAuthorization(options =>
            {
                options.AddPolicy(Constants.SystemCommunicationApiPolicy, policy =>
                    policy.RequireClaim(InfrastructureCommon.ClaimScope, CommonConstants.OctoApiFullAccess));
                options.AddPolicy(Constants.TenantCommunicationApiReadWritePolicy, policy =>
                    policy.RequireClaim(InfrastructureCommon.ClaimScope, CommonConstants.OctoApiFullAccess));
            })
            .Configure<AdapterHubAuthorizationOptions>(o => o.Mode = AdapterHubAuthorizationMode.LogOnly)
            .Configure<OperatorHubAuthorizationOptions>(o => o.Mode = OperatorHubAuthorizationMode.LogOnly)
            .Configure<TenantAuthorizationOptions>(_ => { })
            .BuildServiceProvider();

        var adapterConnected = false;
        await new AdapterHubAuthorizationFilter().OnConnectedAsync(CreateContext(provider, Anonymous()), _ =>
        {
            adapterConnected = true;
            return Task.CompletedTask;
        });
        var operatorConnected = false;
        await new OperatorHubAuthorizationFilter().OnConnectedAsync(CreateContext(provider, Anonymous()), _ =>
        {
            operatorConnected = true;
            return Task.CompletedTask;
        });

        await Assert.That(adapterConnected).IsTrue();
        await Assert.That(operatorConnected).IsTrue();
    }

    /// <summary>
    ///     The exporter only picks up meters it was told about; this one must be the service meter that
    ///     octo-common-services' <c>ObservabilityBuilder</c> registers, or the counter is produced and
    ///     dropped (the AB#5430 failure mode).
    /// </summary>
    /// <summary>
    ///     Every refusal series exists with a zero before the first decision, so increase() in Dash0
    ///     counts the first would_refuse after a restart (see the constructor remarks).
    /// </summary>
    [Test]
    public async Task Construction_WritesAZeroBaselineForEveryRefusalSeries()
    {
        await using var provider = BuildServiceProvider();
        using var recorder = new Recorder(provider);

        provider.GetRequiredService<HubAuthorizationMetrics>();

        string Key(Decision d) => string.Join("|", d.Tags["octo.hub.name"], d.Tags["octo.hub.authorization.mode"],
            d.Tags["octo.hub.authorization.outcome"], d.Tags["octo.hub.authorization.reason"]);
        var keys = recorder.Baseline.Select(Key).ToHashSet();

        await Assert.That(recorder.Decisions.Count).IsEqualTo(0);
        await Assert.That(keys).Contains("adapter|log_only|would_refuse|unauthenticated");
        await Assert.That(keys).Contains("adapter|enforce|refused|tenant_mismatch");
        await Assert.That(keys).Contains("adapter|enforce|allowed|cross_tenant_client");
        await Assert.That(keys).Contains("operator|log_only|would_refuse|missing_scope");
        await Assert.That(keys).Contains("operator|enforce|refused|unauthenticated");
        // Operator hub has no tenant reasons; 7 adapter + 3 operator reasons, both modes.
        await Assert.That(keys.Count).IsEqualTo(20);
    }

    [Test]
    public async Task TwoConnections_AreTwoDecisions()
    {
        await using var provider = BuildServiceProvider();
        using var recorder = new Recorder(provider);

        await ConnectAdapterAsync(CreateContext(provider, ServiceToken()));
        await ConnectAdapterAsync(CreateContext(provider, Anonymous()));

        await Assert.That(recorder.Decisions.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Meter_IsTheServiceMeter()
    {
        await Assert.That(HubAuthorizationMetrics.MeterName).IsEqualTo("Meshmakers.Octo.Communication");
    }

    /// <summary>
    ///     The registration lives in the top-level statements of <c>Program.cs</c>, which a unit test
    ///     cannot compose — pinned at the source, like the hub filter wiring tests do.
    /// </summary>
    [Test]
    public async Task Program_RegistersTheDecisionMetrics()
    {
        var program = await File.ReadAllTextAsync(Path.Combine(RepositoryRoot(),
            "src", "CommunicationControllerServices", "Program.cs"));

        await Assert.That(program).Contains("AddSingleton<HubAuthorizationMetrics>()");
        await Assert.That(program).Contains("app.Services.GetRequiredService<HubAuthorizationMetrics>()");
    }

    private static string RepositoryRoot([CallerFilePath] string sourceFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", "..", ".."));
}
