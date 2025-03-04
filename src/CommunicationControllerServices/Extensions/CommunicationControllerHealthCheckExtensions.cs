using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Extensions;

/// <summary>
/// Adds communication controller health checks
/// </summary>
public static class CommunicationControllerHealthCheckExtensions
{
    private static readonly string[] AdapterTags = ["adapters"];

    /// <summary>
    /// adds the adapter health checks to DI
    /// </summary>
    /// <param name="builder"></param>
    /// <returns></returns>
    public static IHealthChecksBuilder AddAdapterHealthChecks(this IHealthChecksBuilder builder)
    {
        builder.AddCheck<AdapterHealthChecks>(
            "AdapterHealthChecks", 
            HealthStatus.Unhealthy, 
            AdapterTags);
        return builder;
    }
}