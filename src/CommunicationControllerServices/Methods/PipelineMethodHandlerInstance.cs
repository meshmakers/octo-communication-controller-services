using System.Reflection;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Methods;

/// <summary>
///     Identity and announcement state of this controller instance as a CK method handler for pipeline-handled
///     methods (AB#6879). Mirrors the instance of the handler SDK (announce, heartbeat, withdraw) so a dispatcher
///     sees the controller like any other handler.
/// </summary>
internal sealed class PipelineMethodHandlerInstance(
    IOptions<PipelineMethodOptions> options,
    TimeProvider timeProvider)
{
    /// <summary>Name in the handler registry.</summary>
    public const string ServiceName = "communication-controller-services";

    private readonly Dictionary<string, EndpointHandle> _endpoints = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    /// <summary>Unique id of this controller process as a handler.</summary>
    public string InstanceId { get; } = Guid.NewGuid().ToString("N");

    public string? Version { get; } =
        (Assembly.GetEntryAssembly() ?? typeof(PipelineMethodHandlerInstance).Assembly)
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

    /// <summary>True once graceful shutdown started: no further announcement.</summary>
    public bool Withdrawn { get; private set; }

    /// <summary>The method ids with a ready endpoint, i.e. the ones announced.</summary>
    public IReadOnlyList<string> MethodIds
    {
        get
        {
            lock (_lock)
            {
                return _endpoints.Keys.Order(StringComparer.Ordinal).ToList();
            }
        }
    }

    public bool HasEndpoint(string methodId)
    {
        lock (_lock)
        {
            return _endpoints.ContainsKey(methodId);
        }
    }

    public void AddEndpoint(string methodId, EndpointHandle handle)
    {
        lock (_lock)
        {
            _endpoints[methodId] = handle;
        }
    }

    public EndpointHandle? RemoveEndpoint(string methodId)
    {
        lock (_lock)
        {
            return _endpoints.Remove(methodId, out var handle) ? handle : null;
        }
    }

    /// <summary>Marks the instance withdrawn and returns all endpoints for disposal.</summary>
    public IReadOnlyList<(string MethodId, EndpointHandle Handle)> TakeAllEndpoints()
    {
        lock (_lock)
        {
            Withdrawn = true;
            var all = _endpoints.Select(e => (e.Key, e.Value)).ToList();
            _endpoints.Clear();
            return all;
        }
    }

    public CkMethodHandlerAnnounced CreateAnnouncement(IReadOnlyList<string> methodIds) =>
        new(Guid.NewGuid(), timeProvider.GetUtcNow().UtcDateTime)
        {
            ServiceName = ServiceName,
            InstanceId = InstanceId,
            MethodIds = methodIds,
            Tenants = [CkMethodHandlerTenants.All],
            Version = Version,
            HeartbeatIntervalSeconds = (int)Math.Ceiling(options.Value.HeartbeatInterval.TotalSeconds),
            TtlSeconds = (int)Math.Ceiling(options.Value.Ttl.TotalSeconds)
        };

    public CkMethodHandlerWithdrawn CreateWithdrawal(IReadOnlyList<string> methodIds) =>
        new(Guid.NewGuid(), timeProvider.GetUtcNow().UtcDateTime)
        {
            ServiceName = ServiceName,
            InstanceId = InstanceId,
            MethodIds = methodIds
        };
}
