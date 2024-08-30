using Meshmakers.Common.Shared;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Options;
using Meshmakers.Octo.Services.Swagger;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Configuration;

// ReSharper disable once ClassNeverInstantiated.Global
internal class ConfigureOctoSwaggerOptions(IOptions<CommunicationControllerOptions> octoOptions)
    : IConfigureNamedOptions<OctoSwaggerOptions>
{
    public void Configure(OctoSwaggerOptions options)
    {
        Configure(Microsoft.Extensions.Options.Options.DefaultName, options);
    }

    public void Configure(string? name, OctoSwaggerOptions options)
    {
        options.AuthorityUrl = octoOptions.Value.Authority.EnsureEndsWith("/");
    }
}
