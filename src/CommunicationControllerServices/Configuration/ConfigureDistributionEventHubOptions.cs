using Meshmakers.Octo.Backend.CommunicationControllerServices.Options;
using Meshmakers.Octo.Common.DistributionEventHub.Configuration.Options;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Configuration;

// ReSharper disable once ClassNeverInstantiated.Global
internal class ConfigureDistributionEventHubOptions : IConfigureNamedOptions<DistributionEventHubOptions>
{
    private readonly IOptions<CommunicationControllerOptions> _communicationControllerOptions;
    private readonly IOptions<OctoSystemConfiguration> _octoSystemConfiguration;

    public ConfigureDistributionEventHubOptions(IOptions<CommunicationControllerOptions> communicationControllerOptions,
        IOptions<OctoSystemConfiguration> octoSystemConfiguration)
    {
        _communicationControllerOptions = communicationControllerOptions;
        _octoSystemConfiguration = octoSystemConfiguration;
    }


    public void Configure(DistributionEventHubOptions options)
    {
        Configure(Microsoft.Extensions.Options.Options.DefaultName, options);
    }

    public void Configure(string? name, DistributionEventHubOptions options)
    {
        options.BrokerHost = _communicationControllerOptions.Value.BrokerHost;
        options.BrokerUser = _communicationControllerOptions.Value.BrokerUser;
        options.BrokerPassword = _communicationControllerOptions.Value.BrokerPassword;
        options.RepositoryHost = _octoSystemConfiguration.Value.DatabaseHost;
        options.RepositoryUser = _octoSystemConfiguration.Value.DatabaseUser;
        options.RepositoryPassword = _octoSystemConfiguration.Value.DatabaseUserPassword;
        options.DatabaseAuthenticationSource = _octoSystemConfiguration.Value.AuthenticationDatabaseName;
    }
}