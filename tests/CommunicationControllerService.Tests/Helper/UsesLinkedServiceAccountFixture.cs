using Meshmakers.Octo.Backend.CommunicationControllerServices;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Options;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Communication.Contracts;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Commands;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Commands.Payloads;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;

/// <summary>
/// AB#8982 — a tenant as a blueprint leaves it: one adapter with its own (provisioned) pipeline
/// service account, and standalone <c>ServiceAccountConfiguration</c>s that the adapter's pipelines
/// link via <c>Uses</c>, shipped without a client id. Drives the REAL
/// <see cref="PipelineServiceAccountProvisioningService" /> over a substituted repository and identity
/// command client, so the automatic paths (blueprint consumer, tenant load, workload deploy) are
/// tested end to end down to what goes onto the identity wire.
/// </summary>
internal sealed class UsesLinkedServiceAccountFixture
{
    public const string TenantId = "tenantId";

    public ICommunicationRepository Repository { get; } = Substitute.For<ICommunicationRepository>();

    public ICommandClient<CreateIdentityDataCommandRequest> CommandClient { get; } =
        Substitute.For<ICommandClient<CreateIdentityDataCommandRequest>>();

    public ICommunicationEventService EventService { get; } = Substitute.For<ICommunicationEventService>();

    public RtAdapter Adapter { get; } = RtEntityCreator.CreateAdapter();

    public UsesLinkedServiceAccountFixture()
    {
        CommandClient
            .GetResponse<EnumCommandResponse<CreateIdentityDataResult>>(
                Arg.Any<CreateIdentityDataCommandRequest>(), Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>())
            .Returns(new EnumCommandResponse<CreateIdentityDataResult> { Response = CreateIdentityDataResult.Success });

        var adapterAccount = new RtServiceAccountConfiguration
        {
            RtId = OctoObjectId.GenerateNewId(),
            CkTypeId = SystemCommunicationCkIds.RtCkServiceAccountConfigurationTypeId,
            RtWellKnownName = PipelineServiceAccountProvisioningService.BuildWellKnownName(Adapter.RtId),
            ClientId = PipelineServiceAccountProvisioningService.BuildClientId(Adapter.RtId),
            ClientSecret = SecretTestValues.Stored("adapter-secret"),
            AssignedRoleNames = new AttributeStringValueList([CommonConstants.CommunicationManagementRole]),
            AllowDelegation = true
        };
        Repository.GetAdaptersAsync(TenantId).Returns([Adapter]);
        Repository.GetServiceAccountForAdapterAsync(TenantId, Adapter.RtId).Returns(adapterAccount);
        Repository.GetServiceAccountByWellKnownNameAsync(TenantId, adapterAccount.RtWellKnownName!)
            .Returns(adapterAccount);
        Repository.GetAdapterForServiceAccountAsync(TenantId, adapterAccount.RtId).Returns(Adapter);
        Accounts.Add(adapterAccount);
        Repository.GetServiceAccountsAsync(TenantId).Returns(_ => Accounts.ToList());
    }

    /// <summary>All configurations of the tenant, adapter-owned and standalone.</summary>
    public List<RtServiceAccountConfiguration> Accounts { get; } = [];

    /// <summary>The adapter's own client id — the impersonation actor of the standalone accounts.</summary>
    public string AdapterClientId => PipelineServiceAccountProvisioningService.BuildClientId(Adapter.RtId);

    /// <summary>
    /// Adds a standalone account as a blueprint ships it (no client id, no secret, declared roles),
    /// linked via <c>Uses</c> by one pipeline running on <paramref name="runsOn" /> (default: the
    /// fixture's adapter); <c>linked: false</c> leaves it unused.
    /// </summary>
    public RtServiceAccountConfiguration AddStandalone(string wellKnownName, string[] roles, bool linked = true,
        RtAdapter? runsOn = null, string? clientId = null)
    {
        var configuration = new RtServiceAccountConfiguration
        {
            RtId = OctoObjectId.GenerateNewId(),
            CkTypeId = SystemCommunicationCkIds.RtCkServiceAccountConfigurationTypeId,
            RtWellKnownName = wellKnownName,
            AssignedRoleNames = new AttributeStringValueList(roles.ToList()),
            AllowDelegation = false
        };
        if (clientId != null)
        {
            configuration.ClientId = clientId;
        }

        Repository.GetAdapterForServiceAccountAsync(TenantId, configuration.RtId).Returns((RtAdapter?)null);
        if (linked)
        {
            var pipeline = RtEntityCreator.CreatePipeline();
            Repository.GetPipelinesUsingServiceAccountAsync(TenantId, configuration.RtId).Returns([pipeline]);
            Repository.GetAdapterByPipelineAsync(TenantId, pipeline.ToRtEntityId()).Returns(runsOn ?? Adapter);
        }
        else
        {
            Repository.GetPipelinesUsingServiceAccountAsync(TenantId, configuration.RtId).Returns([]);
        }

        Accounts.Add(configuration);
        return configuration;
    }

    public PipelineServiceAccountProvisioningService CreateProvisioningService()
    {
        var services = new ServiceCollection();
        services.AddSingleton(CommandClient);
        return new PipelineServiceAccountProvisioningService(
            Repository,
            new PipelineServiceAccountResolver(Repository),
            services.BuildServiceProvider(),
            EventService,
            Options.Create(new CommunicationControllerOptions
            {
                AuthorityUrl = "https://identity.example.com",
                PublicUrl = "https://communication.example.com"
            }),
            TestSecretEncryption.Create().EncryptionService);
    }

    /// <summary>Every identity client sent onto the wire, in order.</summary>
    public IReadOnlyList<DistClientDto> SentClients()
    {
        return CommandClient.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(ICommandClient<CreateIdentityDataCommandRequest>.GetResponse))
            .SelectMany(c => ((CreateIdentityDataCommandRequest)c.GetArguments()[0]!).Clients ?? [])
            .ToList();
    }

    /// <summary>The configuration entities written back for standalone accounts.</summary>
    public IReadOnlyList<RtServiceAccountConfiguration> UpdatedStandalone()
    {
        return Repository.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(ICommunicationRepository.UpdateServiceAccountAsync))
            .Select(c => (RtServiceAccountConfiguration)c.GetArguments()[1]!)
            .ToList();
    }
}
