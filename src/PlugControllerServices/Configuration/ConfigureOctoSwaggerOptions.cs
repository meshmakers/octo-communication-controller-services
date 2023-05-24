using Meshmakers.Common.Shared;
using Meshmakers.Octo.Backend.PlugControllerServices.Options;
using Meshmakers.Octo.Backend.Swagger;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.PlugControllerServices.Configuration;

// ReSharper disable once ClassNeverInstantiated.Global
internal class ConfigureOctoSwaggerOptions : IConfigureNamedOptions<OctoSwaggerOptions>
{
    private readonly IOptions<PlugControllerOptions> _octoOptions;

    public ConfigureOctoSwaggerOptions(IOptions<PlugControllerOptions> octoOptions)
    {
        _octoOptions = octoOptions;
    }

    public void Configure(OctoSwaggerOptions options)
    {
        Configure(Microsoft.Extensions.Options.Options.DefaultName, options);
    }

    public void Configure(string? name, OctoSwaggerOptions options)
    {
        options.AuthorityUrl = _octoOptions.Value.Authority.EnsureEndsWith("/");
    }
}
