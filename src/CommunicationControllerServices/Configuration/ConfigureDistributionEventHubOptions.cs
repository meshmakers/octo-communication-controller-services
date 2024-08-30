using Meshmakers.Octo.Backend.CommunicationControllerServices.Options;
using Meshmakers.Octo.Common.DistributionEventHub.Configuration.Options;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Configuration;

// ReSharper disable once ClassNeverInstantiated.Global
internal class ConfigureDistributionEventHubOptions(
    IOptions<CommunicationControllerOptions> communicationControllerOptions,
    IOptions<OctoSystemConfiguration> octoSystemConfiguration)
    : IConfigureNamedOptions<DistributionEventHubOptions>
{
    public void Configure(DistributionEventHubOptions options)
    {
        Configure(Microsoft.Extensions.Options.Options.DefaultName, options);
    }

    public void Configure(string? name, DistributionEventHubOptions options)
    {
        options.BrokerHost = communicationControllerOptions.Value.BrokerHost;
        options.BrokerUser = communicationControllerOptions.Value.BrokerUser;
        options.BrokerPassword = communicationControllerOptions.Value.BrokerPassword;
        options.RepositoryHost = octoSystemConfiguration.Value.DatabaseHost;
        options.RepositoryUser = octoSystemConfiguration.Value.DatabaseUser;
        options.RepositoryPassword = octoSystemConfiguration.Value.DatabaseUserPassword;
        options.DatabaseAuthenticationSource = octoSystemConfiguration.Value.AuthenticationDatabaseName;
    }
}