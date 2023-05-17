using Meshmakers.Octo.Backend.DeviceManagementServices.Options;
using Meshmakers.Octo.Backend.DistributedCache;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.DeviceManagementServices.Configuration;

// ReSharper disable once ClassNeverInstantiated.Global
internal class ConfigureDistributeCacheWithPubSubOptions : IConfigureNamedOptions<DistributeCacheWithPubSubOptions>
{
    private readonly IOptions<OctoDeviceManagementOptions> _octoDeviceManagementOptions;

    public ConfigureDistributeCacheWithPubSubOptions(IOptions<OctoDeviceManagementOptions> octoDeviceManagementOptions)
    {
        _octoDeviceManagementOptions = octoDeviceManagementOptions;
    }


    public void Configure(DistributeCacheWithPubSubOptions options)
    {
        Configure(Microsoft.Extensions.Options.Options.DefaultName, options);
    }

    public void Configure(string? name, DistributeCacheWithPubSubOptions options)
    {
        options.Host = _octoDeviceManagementOptions.Value.RedisCacheHost;
        options.Password = _octoDeviceManagementOptions.Value.RedisCachePassword;
    }
}
