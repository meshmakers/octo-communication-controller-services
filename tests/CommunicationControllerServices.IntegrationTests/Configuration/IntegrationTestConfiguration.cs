using Microsoft.Extensions.Configuration;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.IntegrationTests.Configuration;

/// <summary>
/// Configuration builder for integration tests.
/// </summary>
public class IntegrationTestConfiguration : IConfiguration
{
    private readonly IConfigurationRoot _configurationRoot;

    public IntegrationTestConfiguration()
    {
        var builder = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.test.json", optional: false)
            .AddEnvironmentVariables();

        _configurationRoot = builder.Build();
    }

    public string? this[string key]
    {
        get => _configurationRoot[key];
        set => _configurationRoot[key] = value;
    }

    public IEnumerable<IConfigurationSection> GetChildren() => _configurationRoot.GetChildren();

    public Microsoft.Extensions.Primitives.IChangeToken GetReloadToken() => _configurationRoot.GetReloadToken();

    public IConfigurationSection GetSection(string key) => _configurationRoot.GetSection(key);
}
