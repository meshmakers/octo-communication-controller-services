using Meshmakers.Octo.Backend.CommunicationControllerServices.Options;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Configuration;

internal class ConfigureOpenIdConnectOptions(IOptions<CommunicationControllerOptions> communicationControllerOptions)
    : IConfigureNamedOptions<OpenIdConnectOptions>
{
    public void Configure(OpenIdConnectOptions options)
    {
        Configure(Microsoft.Extensions.Options.Options.DefaultName, options);
    }

    public void Configure(string? name, OpenIdConnectOptions options)
    {
        options.Authority = communicationControllerOptions.Value.AuthorityUrl;
    }
}