using MartinCostello.Logging.XUnit;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Adapters;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Pools;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Hubs;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Options;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.Communication.Contracts.Hubs;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Services;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Services.Defaults;
using Meshmakers.Octo.Communication.Contracts.MessageObjects;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Commands;
using Meshmakers.Octo.Services.Infrastructure;
using Meshmakers.Octo.Services.Infrastructure.Services;
using Meshmakers.Octo.Services.Notifications.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.IntegrationTests.Fixtures;

/// <summary>
/// Base fixture that provides a service collection and service provider.
/// This is the foundation for all integration test fixtures.
/// </summary>
public abstract class ServiceCollectionFixture : ITestOutputHelperAccessor, IAsyncLifetime
{
    private bool _isInitialized;

    protected ServiceCollectionFixture()
    {
        Services = new ServiceCollection();

        // Add infrastructure with short service name (MongoDB app name limit is 128 bytes)
        Services.AddOctoServiceInfrastructure("CommCtrlTests", _ => { });

        // Add CK models FIRST (order matters: base models first, then dependent models)
        // IMPORTANT: CK models must be registered before AddMongoDbRuntimeRepository()
        // to ensure BSON class maps are available for typed entity deserialization
        Services.AddCkModelSystemV2();
        Services.AddCkModelSystemBotV3();
        Services.AddCkModelSystemCommunicationV3();
        Services.AddCkModelSystemNotificationV2();

        // Add runtime engine with MongoDB AFTER CK models
        Services.AddRuntimeEngine()
            .AddMongoDbRuntimeRepository();

        // Add communication controller services
        Services.AddSingleton<ICommunicationRepository, CommunicationRepository>();
        Services.AddSingleton<ICommunicationEventService, CommunicationEventService>();
        Services.AddSingleton<IPipelineSchemaValidator, PipelineSchemaValidator>();
        Services.AddSingleton<IPipelineDefinitionService, PipelineDefinitionService>();
        // AB#4984: AdapterService/PoolService take the on-demand capability service. The real
        // implementation is pure over the repository/cache/parser registered above, so the
        // integration tests exercise the real trigger classification.
        Services.AddSingleton<IWorkloadOnDemandCapabilityService, WorkloadOnDemandCapabilityService>();
        // AB#5027: AdapterService takes the pipeline service-account resolver. Real implementation
        // over the repository registered above, so the association traversal is exercised for real.
        Services.AddSingleton<IPipelineServiceAccountResolver, PipelineServiceAccountResolver>();
        // AB#5111: AdapterService takes the workload template resolver to resolve the IssuerUri
        // deploy-time token in the service-account projection. Real implementation over default
        // (empty) options — with no ServiceUrls configured the {{service.authority}} token falls
        // back to AuthorityUrl, which is exactly the local-dev shape.
        Services.AddOptions<CommunicationControllerOptions>();
        Services.AddSingleton<IWorkloadTemplateResolver, WorkloadTemplateResolver>();
        // AB#5027 phase 2: PoolService takes the provisioning service. Substituted here — the real
        // one talks to the identity service over the distribution event hub, which the integration
        // fixture does not run; the repository-side entity + edge write it performs is covered
        // directly by Repository/PipelineServiceAccountRepositoryTests.
        Services.AddSingleton(Substitute.For<IPipelineServiceAccountProvisioningService>());
        // AB#5112: AdapterService takes the identity-client reader (hardened deploy guard) and the
        // guard options. Substituted for the same reason as the provisioning service above — the
        // real reader talks HTTP to the identity service, which the fixture does not run. It is
        // stubbed to answer Unavailable (a bare substitute would return null and NRE), which the
        // guard treats as non-blocking by contract — exactly the identity-less deployment shape.
        var identityClientReader = Substitute.For<IIdentityClientReader>();
        identityClientReader
            .GetClientAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>())
            .Returns(IdentityClientLookup.Unavailable("integration fixture runs no identity service"));
        Services.AddSingleton(identityClientReader);
        Services.AddOptions<ServiceAccountGuardOptions>();
        Services.AddSingleton<IServiceAccountHealthService, ServiceAccountHealthService>();
        // AB#5113: the rights analysis is pure over the repository/resolver/parser registered
        // above — real implementation, no identity REST involved (the System.Identity reads go
        // through the tenant repository).
        Services.AddSingleton<IServiceAccountRightsAnalysisService, ServiceAccountRightsAnalysisService>();
        Services.AddSingleton<IAdapterConnectionTracker, AdapterConnectionTracker>();
        Services.AddSingleton<IAdapterService, AdapterService>();
        Services.AddSingleton<IPoolService, PoolService>();
        Services.AddSingleton<IPipelineDebugService, PipelineDebugService>();
        Services.AddTransient<ITriggerManagementService, TriggerManagementService>();

        Services.AddScopedMultipleInterfaces<DefaultConfigurationCreatorService, IDefaultConfigurationCreatorService,
            IConfigurationService>();
        Services.AddSingletonMultipleInterfaces<PoolHubCache, IPoolCache, IPoolCachePublish>();
        Services.AddSingletonMultipleInterfaces<AdapterCache, IAdapterCache, IAdapterCachePublish>();

        // Legacy IPoolHubCallbacks/PoolHubCallbacks were removed when /poolHub
        // collapsed into /operatorHub — tenant pre-update fan-out now flows
        // through IOperatorConnectionManager.
        Services.AddSingleton<IAdapterHubCallbacks, AdapterHubCallbacks>();

        // Add notification services
        Services.AddScoped<IEventRepository, EventRepository>();

        // Reset tenant notification to default implementation without using RabbitMQ
        Services.AddSingleton<ITenantNotifications, DefaultTenantNotifications>();

        // Add mock command clients (required by TriggerManagementService and DefaultConfigurationCreatorService)
        // These are normally provided by the distribution event hub but are not needed for integration tests
        Services.AddSingleton(Substitute.For<ICommandClient<RemoveRecurringJobsByScheduleGroupRequest>>());
        Services.AddSingleton(Substitute.For<ICommandClient<CreateIdentityDataCommandRequest>>());
        Services.AddSingleton(Substitute.For<IRoutedCommandClient<ExecutePipelineRequest>>());
        Services.AddSingleton(Substitute.For<IDistributionEventHubService>());

        // AB#4918: AdapterService and TriggerManagementService take the on-demand lifecycle
        // service (wake gates / Configured hook). The integration tests exercise repository and
        // config flows, not the lifecycle state machine — a substitute keeps every gate a no-op
        // (same pattern as the command clients above).
        Services.AddSingleton(Substitute.For<IWorkloadLifecycleService>());

        // Add mock SignalR hub contexts (required by hub callbacks). PoolHub
        // is gone — its responsibilities collapsed into OperatorHub.
        Services.AddSingleton(Substitute.For<IHubContext<AdapterHub>>());
        Services.AddSingleton(Substitute.For<IHubContext<OperatorHub>>());

        // Add logging with xUnit output
        Services.AddLogging(loggingBuilder =>
        {
            loggingBuilder.ClearProviders();
            loggingBuilder.SetMinimumLevel(LogLevel.Trace);
            loggingBuilder.AddXUnit(this);
        });
    }

    public ServiceCollection Services { get; }

    public ServiceProvider? Provider { get; private set; }

    public Xunit.ITestOutputHelper? OutputHelper { get; set; }

    public void EnsureInitialized()
    {
        if (!_isInitialized)
        {
            throw new InvalidOperationException("Fixture is not initialized. Call InitializeAsync first.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisposeServicesAsync();

        if (Provider is not null)
        {
            await Provider.DisposeAsync();
        }
    }

    public async ValueTask InitializeAsync()
    {
        if (_isInitialized)
        {
            return;
        }

        await InitializeServicesAsync();
    }

    protected virtual Task InitializeServicesAsync()
    {
        Provider = Services.BuildServiceProvider();
        _isInitialized = true;

        return Task.CompletedTask;
    }

    protected abstract Task DisposeServicesAsync();

    public T GetService<T>() where T : notnull
    {
        EnsureInitialized();
        return Provider!.GetRequiredService<T>();
    }

    public ISystemContext GetSystemContext()
    {
        EnsureInitialized();
        return Provider!.GetRequiredService<ISystemContext>();
    }
}
