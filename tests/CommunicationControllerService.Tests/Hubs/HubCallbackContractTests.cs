using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Adapters;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Hubs;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Contracts.Hubs;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Microsoft.AspNetCore.SignalR;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Hubs;

/// <summary>
///     N8 — every controller → client SignalR send must bind to the handler the client registers
///     from the shared callback interface. A send that does not bind is dropped on the client
///     without an error on either side, so nothing but a test like this turns red.
/// </summary>
/// <remarks>
///     Two layers. The runtime tests drive the real senders (<see cref="AdapterHubCallbacks" />,
///     <see cref="OperatorConnectionManager" />; <c>LeaseService</c> in
///     <c>LeaseHubCallbackContractTests</c>) and check argument count and types by reflection. The
///     source sweep covers every call site, including ones no runtime test reaches
///     (<c>AdapterHub</c>'s re-registration request): it counts the arguments of every
///     <c>SendAsync(nameof(I…HubCallbacks.X), …)</c> in <c>src/</c> against the interface, and
///     refuses a hub send whose method name is not taken from the interface at all.
/// </remarks>
internal class HubCallbackContractTests
{
    private const string TenantId = "tenant-a";
    private const string DeploymentSiteRtId = "65d5c447b420da3fb12381cc";
    private const string WorkloadRtId = "667ac108ef60ca86e830e47e";
    private const string ConnectionId = "conn-1";

    private static readonly Type[] CallbackInterfaces =
        [typeof(IAdapterHubCallbacks), typeof(IAdapterPoolHubCallbacks), typeof(IOperatorHubCallbacks)];

    // ---- the checker itself ----

    /// <summary>The exact N8 shape: a one-parameter callback sent with no arguments.</summary>
    [Test]
    public async Task Checker_FlagsAMissingArgument()
    {
        var violations = HubCallbackContract.Violations(typeof(IAdapterPoolHubCallbacks),
            nameof(IAdapterPoolHubCallbacks.DrainAsync), []);

        await Assert.That(violations.Count).IsEqualTo(1);
        await Assert.That(violations[0]).Contains("takes 1 argument(s)");
    }

    [Test]
    public async Task Checker_FlagsAnArgumentOfTheWrongType()
    {
        var violations = HubCallbackContract.Violations(typeof(IAdapterPoolHubCallbacks),
            nameof(IAdapterPoolHubCallbacks.LeaseAsync), ["not a lease"]);

        await Assert.That(violations.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Checker_FlagsAnUnknownMethod()
    {
        var violations = HubCallbackContract.Violations(typeof(IAdapterPoolHubCallbacks), "ShutdownAsync", []);

        await Assert.That(violations.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Checker_AcceptsAMatchingSend()
    {
        var violations = HubCallbackContract.Violations(typeof(IOperatorHubCallbacks),
            nameof(IOperatorHubCallbacks.DeploymentSiteUndeployedAsync), [TenantId, DeploymentSiteRtId]);

        await Assert.That(violations).IsEmpty();
    }

    // ---- runtime: the real senders ----

    [Test]
    public async Task AdapterHubCallbacks_EverySendBindsToTheAdapterHandlers()
    {
        var recorder = new RecordingClientProxy();
        var hub = HubContextSendingTo<AdapterHub>(recorder);
        var cache = new AdapterCache();
        var adapterRtEntityId = new RtEntityId("System.Communication/MeshAdapter", OctoObjectId.GenerateNewId());
        var configuration = new AdapterConfigurationDto(adapterRtEntityId, null, []);
        cache.AddOrUpdateTenant(TenantId).AddAdapter(adapterRtEntityId, ConnectionId, configuration);
        var sut = new AdapterHubCallbacks(hub, cache);

        await sut.AdapterConfigurationUpdatedAsync(TenantId, configuration);
        await sut.PreUpdateTenantAsync(TenantId);
        await sut.CkModelChangedAsync(TenantId);

        await AssertAllBind(typeof(IAdapterHubCallbacks), recorder,
            nameof(IAdapterHubCallbacks.AdapterConfigurationUpdatedAsync),
            nameof(IAdapterHubCallbacks.PreUpdateTenantAsync),
            nameof(IAdapterHubCallbacks.CkModelChangedAsync));
    }

    [Test]
    public async Task OperatorConnectionManager_EverySendBindsToTheOperatorHandlers()
    {
        var recorder = new RecordingClientProxy();
        var sut = new OperatorConnectionManager(HubContextSendingTo<OperatorHub>(recorder));
        sut.AddOperator(ConnectionId);
        sut.RegisterDeploymentSiteForConnection(ConnectionId, TenantId, DeploymentSiteRtId);

        await sut.NotifyDeploymentSiteDeployedAsync(
            new DeployedDeploymentSiteDto { TenantId = TenantId, DeploymentSiteRtId = DeploymentSiteRtId });
        await sut.NotifyWorkloadDeployedAsync(AWorkloadDeploy());
        await sut.NotifyWorkloadScaleAsync(AWorkloadScale());
        await sut.NotifyWorkloadUndeployedAsync(AWorkloadUndeploy());
        await sut.NotifyPreUpdateTenantAsync(TenantId);
        await sut.NotifyDeploymentSiteUndeployedAsync(TenantId, DeploymentSiteRtId);

        await AssertAllBind(typeof(IOperatorHubCallbacks), recorder,
            nameof(IOperatorHubCallbacks.DeploymentSiteDeployedAsync),
            nameof(IOperatorHubCallbacks.WorkloadDeployedAsync),
            nameof(IOperatorHubCallbacks.ScaleWorkloadAsync),
            nameof(IOperatorHubCallbacks.WorkloadUndeployedAsync),
            nameof(IOperatorHubCallbacks.PreUpdateTenantAsync),
            nameof(IOperatorHubCallbacks.DeploymentSiteUndeployedAsync));
    }

    /// <summary>
    ///     The replay picks its method name at runtime from the notification's type — the one send
    ///     whose name the source sweep cannot see. All three kinds go through it here.
    /// </summary>
    [Test]
    public async Task OperatorConnectionManager_ReplayedNotificationsBindToTheOperatorHandlers()
    {
        var recorder = new RecordingClientProxy();
        var sut = new OperatorConnectionManager(HubContextSendingTo<OperatorHub>(recorder));
        sut.AddOperator(ConnectionId);

        // No operator owns the site yet ⇒ queued (AB#4371). Undeploy and deploy share one key
        // (last wins), so they are queued for two different workloads.
        await sut.NotifyWorkloadDeployedAsync(AWorkloadDeploy());
        await sut.NotifyWorkloadUndeployedAsync(AWorkloadUndeploy("686e0f17196fcd3c42ed8c77"));
        await sut.NotifyWorkloadScaleAsync(AWorkloadScale());
        await Assert.That(recorder.Sent).IsEmpty();

        sut.RegisterDeploymentSiteForConnection(ConnectionId, TenantId, DeploymentSiteRtId);
        await sut.FlushPendingWorkloadNotificationsAsync(ConnectionId, TenantId, DeploymentSiteRtId);

        await AssertAllBind(typeof(IOperatorHubCallbacks), recorder,
            nameof(IOperatorHubCallbacks.WorkloadDeployedAsync),
            nameof(IOperatorHubCallbacks.WorkloadUndeployedAsync),
            nameof(IOperatorHubCallbacks.ScaleWorkloadAsync));
    }

    // ---- source sweep: every call site ----

    private static readonly Regex HubSend = new(
        @"\.(?<api>SendAsync|SendCoreAsync)\s*\(", RegexOptions.Compiled);

    private static readonly Regex CallbackName = new(
        @"^nameof\(\s*(?<iface>I\w+HubCallbacks)\.(?<method>\w+)\s*\)$", RegexOptions.Compiled);

    /// <summary>
    ///     Sends whose first argument is not <c>nameof(I…HubCallbacks.X)</c>, each with the reason it
    ///     may be. Anything new that lands here must be justified — a string literal or a computed
    ///     name is exactly how a send drifts from the handler without the compiler noticing.
    /// </summary>
    private static readonly Dictionary<(string File, string FirstArgument), string> NotAHubCallbackSend = new()
    {
        [("WorkloadActivatorMiddleware.cs", "forwarded")] = "HttpClient, not SignalR",
        [("IdentityClientReader.cs", "request")] = "HttpClient, not SignalR",
        [("SignalBridgeClient.cs", "request")] = "HttpClient, not SignalR",
        [("OperatorConnectionManager.cs", "methodName")] =
            "replay; the name comes from a switch over nameof(IOperatorHubCallbacks.*), " +
            "covered by OperatorConnectionManager_ReplayedNotificationsBindToTheOperatorHandlers",
    };

    [Test]
    public async Task EveryHubSendInTheSource_PassesAsManyArgumentsAsItsCallbackTakes()
    {
        var violations = new List<string>();
        var sendsChecked = 0;

        foreach (var file in Directory.EnumerateFiles(SourceRoot(), "*.cs", SearchOption.AllDirectories)
                     .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                                 && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")))
        {
            var source = await File.ReadAllTextAsync(file);
            foreach (Match match in HubSend.Matches(source))
            {
                var arguments = TopLevelArguments(source, match.Index + match.Length);
                var location = $"{Path.GetFileName(file)}:{LineOf(source, match.Index)}";
                if (arguments.Count == 0)
                {
                    violations.Add($"{location}: SendAsync without a method name");
                    continue;
                }

                var callback = CallbackName.Match(arguments[0]);
                if (!callback.Success)
                {
                    if (!NotAHubCallbackSend.ContainsKey((Path.GetFileName(file), arguments[0])))
                    {
                        violations.Add($"{location}: send '{arguments[0]}' does not take its name from an " +
                                       "I…HubCallbacks interface (use nameof, or justify it in NotAHubCallbackSend)");
                    }

                    continue;
                }

                var iface = CallbackInterfaces.SingleOrDefault(t => t.Name == callback.Groups["iface"].Value);
                if (iface is null)
                {
                    violations.Add($"{location}: unknown callback interface {callback.Groups["iface"].Value}");
                    continue;
                }

                var payload = arguments.Skip(1).ToList();
                // SendAsync(name, a, b, cancellationToken): the trailing token is the transport's, not
                // the handler's. SendCoreAsync(name, new object?[]{…}, token) is not used for hub
                // callbacks here; if it ever is, it shows up as a count mismatch and gets looked at.
                if (payload.Count > 0 && LooksLikeACancellationToken(payload[^1]))
                {
                    payload.RemoveAt(payload.Count - 1);
                }

                var method = iface.GetMethods().SingleOrDefault(m => m.Name == callback.Groups["method"].Value);
                if (method is null)
                {
                    violations.Add($"{location}: {iface.Name} has no method {callback.Groups["method"].Value}");
                    continue;
                }

                sendsChecked++;
                if (method.GetParameters().Length != payload.Count)
                {
                    violations.Add($"{location}: {iface.Name}.{method.Name} takes " +
                                   $"{method.GetParameters().Length} argument(s), the send passes {payload.Count} " +
                                   $"({string.Join(", ", payload)})");
                }
            }
        }

        using var _ = Assert.Multiple();
        await Assert.That(violations).IsEmpty();
        // Guards the sweep itself: a path or regex that silently matched nothing would pass above.
        await Assert.That(sendsChecked).IsGreaterThanOrEqualTo(12);
    }

    // ---- helpers ----

    private static async Task AssertAllBind(Type callbacks, RecordingClientProxy recorder, params string[] expected)
    {
        var sent = recorder.Sent;
        using var _ = Assert.Multiple();
        await Assert.That(sent.Select(s => s.Method).ToArray()).IsEquivalentTo(expected);
        foreach (var (method, args) in sent)
        {
            await Assert.That(HubCallbackContract.Violations(callbacks, method, args)).IsEmpty();
        }
    }

    private static IHubContext<THub> HubContextSendingTo<THub>(RecordingClientProxy recorder) where THub : Hub
    {
        var clients = Substitute.For<IHubClients>();
        clients.Client(Arg.Any<string>()).Returns(recorder);
        clients.All.Returns(recorder);
        var hub = Substitute.For<IHubContext<THub>>();
        hub.Clients.Returns(clients);
        return hub;
    }

    private static WorkloadDeployedDto AWorkloadDeploy() => new()
    {
        TenantId = TenantId,
        DeploymentSiteRtId = DeploymentSiteRtId,
        WorkloadRtId = WorkloadRtId,
        WorkloadName = "mesh-adapter",
        WorkloadType = WorkloadTypeDto.Adapter,
        ChartName = "test-chart",
        ChartVersion = "1.0.0",
    };

    private static WorkloadUndeployedDto AWorkloadUndeploy(string workloadRtId = WorkloadRtId) => new()
    {
        TenantId = TenantId,
        DeploymentSiteRtId = DeploymentSiteRtId,
        WorkloadRtId = workloadRtId,
        WorkloadName = "mesh-adapter",
        WorkloadType = WorkloadTypeDto.Adapter,
    };

    private static ScaleWorkloadDto AWorkloadScale() => new()
    {
        TenantId = TenantId,
        DeploymentSiteRtId = DeploymentSiteRtId,
        WorkloadRtId = WorkloadRtId,
        WorkloadName = "mesh-adapter",
        Replicas = 1,
    };

    private static bool LooksLikeACancellationToken(string argument) =>
        argument.Contains("CancellationToken", StringComparison.Ordinal)
        || argument.EndsWith("cancellationToken", StringComparison.Ordinal)
        || argument is "ct" or "token";

    /// <summary>
    ///     Splits the argument list that starts right after an opening parenthesis at its top-level
    ///     commas. Good enough for these call sites; a string literal with a comma or a generic
    ///     argument list with two types would split wrongly — the sweep would then report the site,
    ///     not miss it.
    /// </summary>
    private static List<string> TopLevelArguments(string source, int start)
    {
        var arguments = new List<string>();
        var depth = 0;
        var current = start;
        for (var i = start; i < source.Length; i++)
        {
            var c = source[i];
            switch (c)
            {
                case '(' or '[' or '{':
                    depth++;
                    break;
                case ')' or ']' or '}':
                    if (depth == 0)
                    {
                        AddArgument(arguments, source[current..i]);
                        return arguments;
                    }

                    depth--;
                    break;
                case ',' when depth == 0:
                    AddArgument(arguments, source[current..i]);
                    current = i + 1;
                    break;
            }
        }

        return arguments;
    }

    private static void AddArgument(List<string> arguments, string raw)
    {
        var trimmed = Regex.Replace(raw, @"\s+", " ").Trim();
        if (trimmed.Length > 0)
        {
            arguments.Add(trimmed);
        }
    }

    private static int LineOf(string source, int index) => source.AsSpan(0, index).Count('\n') + 1;

    private static string SourceRoot([CallerFilePath] string sourceFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", "..", "..", "src"));
}
