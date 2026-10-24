using Meshmakers.Octo.Common.DistributionEventHub.Consumers;
using Meshmakers.Octo.Common.DistributionEventHub.Configuration;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Options;
using Meshmakers.Octo.Services.Infrastructure.Initialization;
using Meshmakers.Octo.Services.Methods.Authorization;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Methods;

/// <summary>
///     First reconcile at startup. Order 40: after <c>TenantStartupInitializationService</c> (30) has started the
///     tenants, so the CK caches are loaded and the communication-enabled tenants are known.
/// </summary>
internal sealed class PipelineMethodHostInitializer(
    PipelineMethodHost host,
    IOptions<PipelineMethodOptions> options,
    ILogger<PipelineMethodHostInitializer> logger) : IAsyncInitializationService
{
    public int Order => 40;

    public async Task InitializeAsync()
    {
        if (!options.Value.Enabled)
        {
            logger.LogInformation("The CK pipeline method handler is disabled (PipelineMethods:Enabled=false)");
            return;
        }

        try
        {
            await host.ReconcileAsync(true).ConfigureAwait(false);
        }
        catch (InvalidOperationException e) when (options.Value.FailStartupOnInvalidMethod)
        {
            logger.LogCritical(e, "Startup failed: {Message}", e.Message);
            throw;
        }
        catch (Exception e) when (!options.Value.FailStartupOnInvalidMethod)
        {
            // the controller serves much more than methods: a problem here must not take it down
            logger.LogError(e, "The CK pipeline method handler could not start; it retries on the next heartbeat");
        }
    }
}

/// <summary>Heartbeat: re-checks the pipelines and re-announces every interval; withdraws on graceful shutdown.</summary>
internal sealed class PipelineMethodHeartbeatService(
    PipelineMethodHost host,
    IOptions<PipelineMethodOptions> options,
    TimeProvider timeProvider,
    ILogger<PipelineMethodHeartbeatService> logger) : IHostedService, IAsyncDisposable
{
    private readonly CancellationTokenSource _stopping = new();
    private Task? _loop;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (options.Value.Enabled)
        {
            _loop = Task.Run(() => RunAsync(_stopping.Token), CancellationToken.None);
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        if (_loop is not null)
        {
            try
            {
                await _loop.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // host shutdown timeout
            }
        }

        if (options.Value.Enabled)
        {
            await host.WithdrawAllAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(options.Value.HeartbeatInterval, timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    await host.ReconcileAsync(false, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception e)
                {
                    logger.LogWarning(e, "CK pipeline method handler heartbeat failed");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // stopping
        }
    }

    public ValueTask DisposeAsync()
    {
        _stopping.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>Answers a dispatcher's <see cref="CkMethodHandlerDiscoveryRequested" /> with an immediate announcement.</summary>
internal sealed class PipelineMethodDiscoveryConsumer(
    PipelineMethodHost host,
    ILogger<PipelineMethodDiscoveryConsumer> logger) : IDistributedConsumer<CkMethodHandlerDiscoveryRequested>
{
    public async Task ConsumeAsync(IDistributedContext<CkMethodHandlerDiscoveryRequested> context)
    {
        logger.LogDebug("CK method discovery requested by {RequestingInstanceId}",
            context.Message.RequestingInstanceId);
        try
        {
            await host.AnnounceNowAsync().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Answering the CK method discovery request failed");
        }
    }
}

/// <summary>Registration of the pipeline method handler (AB#6879).</summary>
internal static class PipelineMethodServiceCollectionExtensions
{
    /// <summary>
    ///     Registers the pipeline method handler host, invoker and options (section <c>PipelineMethods</c>).
    ///     The discovery consumer is registered with <see cref="AddPipelineMethodDiscovery" /> in the event hub
    ///     configuration.
    /// </summary>
    public static IServiceCollection AddPipelineMethodHandler(this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<PipelineMethodOptions>(configuration.GetSection(PipelineMethodOptions.SectionName));
        // The forwarded caller token is re-validated here with the settings of the HTTP API of this service (shared
        // helper of octo-common-services). Authority defaults to CommunicationController:AuthorityUrl;
        // Methods:Token overrides it.
        services.AddCkMethodCallerTokenValidation();
        services.AddOptions<CkMethodTokenOptions>()
            .Configure<IOptions<CommunicationControllerOptions>>((token, controller) =>
            {
                if (string.IsNullOrWhiteSpace(token.Authority) && string.IsNullOrWhiteSpace(token.MetadataAddress))
                {
                    token.Authority = controller.Value.AuthorityUrl;
                }
            });
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IPipelineMethodCatalog, PipelineMethodCatalog>();
        services.AddSingleton<PipelineMethodHandlerInstance>();
        services.AddSingleton<IPipelineMethodInvoker, PipelineMethodInvoker>();
        services.AddSingleton<PipelineMethodBusConsumer>();
        services.AddSingleton<PipelineMethodHost>();
        services.AddInitializationService<PipelineMethodHostInitializer>();
        services.AddHostedService<PipelineMethodHeartbeatService>();
        return services;
    }

    /// <summary>Lets the controller answer discovery requests of dispatchers (call in the event hub configuration).</summary>
    public static void AddPipelineMethodDiscovery(this IDistributionEventHubConfiguration configuration)
    {
        configuration.AddBroadcastEventConsumer<PipelineMethodDiscoveryConsumer, CkMethodHandlerDiscoveryRequested>();
    }
}
