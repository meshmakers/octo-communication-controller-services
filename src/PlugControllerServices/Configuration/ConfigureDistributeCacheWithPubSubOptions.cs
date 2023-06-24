using Meshmakers.Octo.Backend.DistributedCache;
using Meshmakers.Octo.Backend.PlugControllerServices.Options;
using Meshmakers.Octo.Common.DistributedCache;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.PlugControllerServices.Configuration;

// ReSharper disable once ClassNeverInstantiated.Global
internal class ConfigureDistributeCacheWithPubSubOptions : IConfigureNamedOptions<DistributeCacheWithPubSubOptions>
{
    private readonly IOptions<PlugControllerOptions> _octoPlugControllerOptions;

    public ConfigureDistributeCacheWithPubSubOptions(IOptions<PlugControllerOptions> octoPlugControllerOptions)
    {
        _octoPlugControllerOptions = octoPlugControllerOptions;
    }


    public void Configure(DistributeCacheWithPubSubOptions options)
    {
        Configure(Microsoft.Extensions.Options.Options.DefaultName, options);
    }

    public void Configure(string? name, DistributeCacheWithPubSubOptions options)
    {
        options.Host = _octoPlugControllerOptions.Value.RedisCacheHost;
        options.Password = _octoPlugControllerOptions.Value.RedisCachePassword;
    }
}
