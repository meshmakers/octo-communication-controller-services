using Meshmakers.Common.Shared;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Options;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Configuration;

internal class ConfigureJwtBearerOptions(
    IOptions<CommunicationControllerOptions> communicationControllerOptions)
    : IConfigureNamedOptions<JwtBearerOptions>
{
    public void Configure(JwtBearerOptions options)
    {
        Configure(Microsoft.Extensions.Options.Options.DefaultName, options);
    }

    public void Configure(string? name, JwtBearerOptions options)
    {
        options.Authority = communicationControllerOptions.Value.AuthorityUrl.EnsureEndsWith("/");
    }
}