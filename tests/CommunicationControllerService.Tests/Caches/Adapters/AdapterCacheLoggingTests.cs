using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.DeploymentSiteServiceTests;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Adapters;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Messages;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Messages.Payloads;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Caches.Adapters;

/// <summary>
/// AB#5537 — the adapter cache must not dump the configuration it reloads: its pipeline
/// configurations carry revealed credentials. Only tenant and adapter count are logged.
/// </summary>
internal class AdapterCacheLoggingTests
{
    [Test]
    [NotInParallel(nameof(DeployWorkloadServiceAccountCredentialsTests))]
    public async Task ReloadConfiguration_LogsNoSerialisedConfiguration()
    {
        var memoryTarget = new NLog.Targets.MemoryTarget("adapter-cache-probe") { Layout = "${message}" };
        var previousConfiguration = NLog.LogManager.Configuration;
        var probeConfiguration = new NLog.Config.LoggingConfiguration();
        probeConfiguration.AddRule(NLog.LogLevel.Trace, NLog.LogLevel.Fatal, memoryTarget);
        NLog.LogManager.Configuration = probeConfiguration;
        try
        {
            var cache = new AdapterCache();
            await cache.ReloadConfigurationAsync(new ComControllerAdapterUpdate("tenant-a", Guid.NewGuid(),
                DateTime.UtcNow, Array.Empty<AdapterDescription>()));

            var line = memoryTarget.Logs.Single(l => l.Contains("Reloading AdapterCache", StringComparison.Ordinal));
            using var _ = Assert.Multiple();
            await Assert.That(line).Contains("tenant-a");
            await Assert.That(line.Contains('{')).IsFalse();
            await Assert.That(line.Contains("correlationId", StringComparison.OrdinalIgnoreCase)).IsFalse();
        }
        finally
        {
            NLog.LogManager.Configuration = previousConfiguration;
        }
    }
}
