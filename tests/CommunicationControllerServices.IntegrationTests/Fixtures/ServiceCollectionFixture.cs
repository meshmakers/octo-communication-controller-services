using MartinCostello.Logging.XUnit;
using MassTransit;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Adapters;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Pools;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Extensions;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Hubs;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.Communication.Contracts.Hubs;
using SystemBotCkModel.Generated.System.Bot.v2;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v2;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Extensions;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Services;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Services.Defaults;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Commands;
using Meshmakers.Octo.Services.Infrastructure;
using Meshmakers.Octo.Services.Infrastructure.Services;
using Meshmakers.Octo.Services.Notifications.Generated.System.Notification.v2;
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

        // Add runtime engine with MongoDB
        Services.AddRuntimeEngine()
            .AddMongoDbRuntimeRepository();

        // Add CK models (order matters: base models first, then dependent models)
        Services.AddCkModelSystemV2();
        Services.AddCkModelSystemBotV2();
        Services.AddCkModelSystemCommunicationV2();
        Services.AddCkModelSystemNotificationV2();

        // Add communication controller services
        Services.AddSingleton<ICommunicationRepository, CommunicationRepository>();
        Services.AddSingleton<ICommunicationEventService, CommunicationEventService>();
        Services.AddSingleton<IAdapterService, AdapterService>();
        Services.AddSingleton<IPoolService, PoolService>();
        Services.AddSingleton<IPipelineDebugService, PipelineDebugService>();
        Services.AddTransient<ITriggerManagementService, TriggerManagementService>();

        Services.AddScopedMultipleInterfaces<DefaultConfigurationCreatorService, IDefaultConfigurationCreatorService,
            IConfigurationService>();
        Services.AddSingletonMultipleInterfaces<PoolHubCache, IPoolCache, IPoolCachePublish>();
        Services.AddSingletonMultipleInterfaces<AdapterCache, IAdapterCache, IAdapterCachePublish>();

        Services.AddSingleton<IPoolHubCallbacks, PoolHubCallbacks>();
        Services.AddSingleton<IAdapterHubCallbacks, AdapterHubCallbacks>();

        // Add notification services
        Services.AddScoped<IEventRepository, EventRepository>();

        // Reset tenant notification to default implementation without using RabbitMQ
        Services.AddSingleton<ITenantNotifications, DefaultTenantNotifications>();

        // Add mock command clients (required by TriggerManagementService and DefaultConfigurationCreatorService)
        // These are normally provided by the distribution event hub but are not needed for integration tests
        Services.AddSingleton(Substitute.For<ICommandClient<RemoveRecurringJobsByScheduleGroupRequest>>());
        Services.AddSingleton(Substitute.For<ICommandClient<CreateIdentityDataCommandRequest>>());
        Services.AddSingleton(Substitute.For<IRoutedCommandClient<ExecuteMeshPipelineRequest>>());
        Services.AddSingleton(Substitute.For<IDistributionEventHubService>());

        // Add mock SignalR hub contexts (required by hub callbacks)
        Services.AddSingleton(Substitute.For<IHubContext<PoolHub>>());
        Services.AddSingleton(Substitute.For<IHubContext<AdapterHub>>());

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
