using Meshmakers.Common.Shared;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Options;
using Meshmakers.Octo.Services.Swagger;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Configuration;

// ReSharper disable once ClassNeverInstantiated.Global
internal class ConfigureOctoSwaggerOptions : IConfigureNamedOptions<OctoSwaggerOptions>
{
    private readonly IOptions<CommunicationControllerOptions> _octoOptions;

    public ConfigureOctoSwaggerOptions(IOptions<CommunicationControllerOptions> octoOptions)
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
