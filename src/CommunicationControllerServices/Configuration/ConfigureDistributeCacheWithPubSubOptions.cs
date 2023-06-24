using Meshmakers.Octo.Backend.CommunicationControllerServices.Options;
using Meshmakers.Octo.Common.DistributedCache;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Configuration;

// ReSharper disable once ClassNeverInstantiated.Global
internal class ConfigureDistributeCacheWithPubSubOptions : IConfigureNamedOptions<DistributeCacheWithPubSubOptions>
{
    private readonly IOptions<CommunicationControllerOptions> _octoCommunicationControllerOptions;

    public ConfigureDistributeCacheWithPubSubOptions(IOptions<CommunicationControllerOptions> octoCommunicationControllerOptions)
    {
        _octoCommunicationControllerOptions = octoCommunicationControllerOptions;
    }


    public void Configure(DistributeCacheWithPubSubOptions options)
    {
        Configure(Microsoft.Extensions.Options.Options.DefaultName, options);
    }

    public void Configure(string? name, DistributeCacheWithPubSubOptions options)
    {
        options.Host = _octoCommunicationControllerOptions.Value.RedisCacheHost;
        options.Password = _octoCommunicationControllerOptions.Value.RedisCachePassword;
    }
}
