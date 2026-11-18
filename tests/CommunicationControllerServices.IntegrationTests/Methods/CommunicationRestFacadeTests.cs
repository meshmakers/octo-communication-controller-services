using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using FluentAssertions;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Hubs;
using Meshmakers.Octo.Backend.CommunicationControllerServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Methods.Service;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Models;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Backend.CommunicationControllerServices.TenantApi.v1.Controllers;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Contracts.MessageObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Meshmakers.Octo.Services.Infrastructure.Initialization;
using Meshmakers.Octo.Services.Methods;
using Meshmakers.Octo.Services.Methods.Audit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.IntegrationTests.Methods;

/// <summary>
///     AB#9048 / AB#9049: the 15 adapter, pool, pipeline, data-flow and workload operation endpoints as facades of their
///     System.Communication methods, through the real facade, the real dispatcher and the real method host of
///     octo-common-services (in-process transport, invocation pipeline, audit filter), with the definitions from the CK cache
///     of a real MongoDB tenant that imported the System.Communication this service embeds. The services behind the handlers
///     are substitutes, so the tests see which service ran and how often.
/// </summary>
/// <remarks>
///     Decision B (Odin, 2026-11-18): a caller with the declared role (<c>CommunicationManagement</c>) goes through the method;
///     a caller who passes the REST policy (scope) without the role keeps the REST code on the guarded direct path. A token
///     of another tenant is refused on both paths and nothing is called.
/// </remarks>
[Collection("CommunicationController")]
public sealed class CommunicationRestFacadeTests(CommunicationControllerFixture fixture) : IAsyncLifetime
{
    private const string CommunicationManagement = "CommunicationManagement";
    private const string Subject = "facade-user";

    private readonly CapturingAudit _audit = new();
    private readonly CapturingLogs _logs = new();
    private readonly ICommunicationRepository _repository = Substitute.For<ICommunicationRepository>();
    private readonly IAdapterService _adapterService = Substitute.For<IAdapterService>();
    private readonly IWorkloadLifecycleService _lifecycle = Substitute.For<IWorkloadLifecycleService>();

    private readonly IPipelineServiceAccountProvisioningService _provisioning =
        Substitute.For<IPipelineServiceAccountProvisioningService>();

    private readonly ICommunicationEventService _events = Substitute.For<ICommunicationEventService>();

    private readonly IAdapterPoolMirrorProvisioningService _mirrors =
        Substitute.For<IAdapterPoolMirrorProvisioningService>();

    private readonly ILeaseService _leases = Substitute.For<ILeaseService>();
    private readonly ILeaseSchedulerService _scheduler = Substitute.For<ILeaseSchedulerService>();
    private readonly ITriggerManagementService _trigger = Substitute.For<ITriggerManagementService>();
    private readonly IHostApplicationLifetime _lifetime = Substitute.For<IHostApplicationLifetime>();
    private readonly CancellationTokenSource _hostStopping = new();
    private ServiceProvider? _provider;

    private string TenantId => fixture.TestTenantId;

    public async ValueTask InitializeAsync()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Methods:ValidationTenantIds:0"] = TenantId,
            ["Methods:Token:Authority"] = "https://identity.test/"
        }).Build();
        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(Environments.Production);
        _lifetime.ApplicationStopping.Returns(_hostStopping.Token);

        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(_logs));
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton(environment);
        services.AddSingleton(fixture.GetService<ICkCacheService>());
        services.AddSingleton(fixture.GetService<IOptions<OctoSystemConfiguration>>());
        services.Configure<CommunicationControllerServices.Options.CommunicationControllerOptions>(o =>
            o.AuthorityUrl = "https://identity.test/");
        services.AddSingleton(_repository).AddSingleton(_adapterService).AddSingleton(_lifecycle)
            .AddSingleton(_provisioning).AddSingleton(_events).AddSingleton(_mirrors).AddSingleton(_leases)
            .AddSingleton(_scheduler).AddSingleton(_trigger).AddSingleton(_lifetime);
        services.AddSingleton(Substitute.For<Meshmakers.Octo.Common.DistributionEventHub.Services.IDistributionEventHubService>());
        services.AddSingleton<ICkMethodAuditRecorder>(_audit);

        services.AddCommunicationCkMethodHost(configuration).Should().BeTrue();
        services.Replace(ServiceDescriptor.Singleton<ICkMethodAuditRecorder>(_audit));
        foreach (var descriptor in services.Where(d => d.ImplementationType?.Name is "CkMethodHandlerStartupService"
                     or "CkMethodHandlerHeartbeatService").ToList())
        {
            services.Remove(descriptor);
        }

        _provider = services.BuildServiceProvider();
        foreach (var initializer in _provider.GetServices<IAsyncInitializationService>()
                     .Where(i => i.GetType().Name == "CkMethodHandlerValidationService"))
        {
            await initializer.InitializeAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_provider != null)
        {
            await _provider.DisposeAsync();
        }

        _hostStopping.Dispose();
    }

    // ------------------------------------------------------------------ the 15 endpoints

    /// <summary>Every facade endpoint with the method it stands for (the parity table of the PR).</summary>
    public static TheoryData<string, string> Endpoints() => new()
    {
        { nameof(AdapterController.DeployUpdate), SystemCommunicationCkIds.AdapterDeployUpdateMethodId },
        { nameof(AdapterController.Wake), SystemCommunicationCkIds.DeployableWorkloadWakeMethodId },
        { nameof(AdapterController.ReconcileServiceAccount), SystemCommunicationCkIds.AdapterReconcileServiceAccountMethodId },
        { nameof(AdapterController.RotateServiceAccountSecret), SystemCommunicationCkIds.AdapterRotateServiceAccountSecretMethodId },
        { nameof(AdapterPoolController.RefreshMirrorsAsync), SystemCommunicationCkIds.AdapterPoolRefreshMirrorsMethodId },
        { nameof(AdapterPoolController.PublishMirrorsAsync), SystemCommunicationCkIds.AdapterPoolPublishMirrorsMethodId },
        { nameof(AdapterPoolController.GrantLeaseAsync), SystemCommunicationCkIds.AdapterPoolGrantLeaseMethodId },
        { nameof(AdapterPoolController.CancelQueuedExecutionAsync), SystemCommunicationCkIds.AdapterPoolCancelQueuedExecutionMethodId },
        { nameof(PipelineController.DeployPipeline), SystemCommunicationCkIds.PipelineDeployMethodId },
        { nameof(PipelineController.ExecutePipeline), SystemCommunicationCkIds.PipelineExecuteMethodId },
        { nameof(PipelineController.MovePipelinesToAdapter), SystemCommunicationCkIds.PipelineMoveToAdapterMethodId },
        { nameof(PipelineController.SetPipelineDebugging), SystemCommunicationCkIds.PipelineSetDebuggingMethodId },
        { nameof(DataFlowController.DeployDataFlow), SystemCommunicationCkIds.DataFlowDeployMethodId },
        { nameof(DataFlowController.UndeployDataFlow), SystemCommunicationCkIds.DataFlowUndeployMethodId },
        { nameof(WorkloadController.UpdateChartVersion), SystemCommunicationCkIds.DeployableWorkloadSetChartVersionMethodId }
    };

    [Fact]
    public void TheEndpointTable_CoversEveryHostedMethod()
    {
        Endpoints().Select(row => row.Data.Item2).Should()
            .BeEquivalentTo(CommunicationMethodHost.Handlers.Select(h => h.MethodId));
    }

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task ACallerWithTheDeclaredRole_GoesThroughTheMethod_InProcess_WithExactlyOneAuditEvent(string endpoint,
        string methodId)
    {
        var result = await CallAsync(endpoint, Caller([CommunicationManagement]));

        StatusOf(result).Should().BeLessThan(300, endpoint);
        ServiceCalls(endpoint).Should().Be(1, "the handler ran the operation once");
        var record = _audit.Records.Should().ContainSingle("one Method audit event per REST call").Subject;
        record.MethodId.Should().Be(methodId);
        record.Transport.Should().Be("in-process");
        record.Outcome.Should().Be(CkMethodAuditOutcomes.Success);
        record.CallerSubject.Should().Be(Subject);
        _logs.All.Should().NotContain("ran on the fallback");
    }

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task ACallerWithTheScopeButWithoutTheRole_KeepsTheRestBehaviour_OnTheGuardedDirectPath(string endpoint,
        string methodId)
    {
        var result = await CallAsync(endpoint, Caller(["Development"]));

        StatusOf(result).Should().BeLessThan(300, "decision B: no REST right is narrowed");
        ServiceCalls(endpoint).Should().Be(1);
        var record = _audit.Records.Should().ContainSingle().Subject;
        record.MethodId.Should().Be(methodId);
        record.Transport.Should().Be(CommunicationMethodFacade.FallbackTransport);
        record.Outcome.Should().Be(CkMethodAuditOutcomes.Success);
        _logs.All.Should().Contain("Reason=role-missing");
    }

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task ATokenOfAnotherTenant_IsRefusedWith403_OnTheMethodPath_AndNothingIsCalled(string endpoint,
        string methodId)
    {
        var result = await CallAsync(endpoint, Caller([CommunicationManagement], tenant: "another-tenant"));

        StatusOf(result).Should().Be(StatusCodes.Status403Forbidden, endpoint);
        ((ErrorResponse)((ObjectResult)result).Value!).ErrorMessage.Should().Contain("tenant-not-allowed");
        AssertNothingWasCalled();
        _audit.Records.Should().ContainSingle().Which.Outcome.Should().Be(CkMethodErrorCodes.MethodForbidden);
        _audit.Records.Single().MethodId.Should().Be(methodId);
    }

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task ATokenOfAnotherTenant_IsRefusedWith403_OnTheGuardedDirectPath_AndNothingIsCalled(string endpoint,
        string methodId)
    {
        var result = await CallAsync(endpoint, Caller(["Development"], tenant: "another-tenant"));

        StatusOf(result).Should().Be(StatusCodes.Status403Forbidden, endpoint);
        AssertNothingWasCalled();
        var record = _audit.Records.Should().ContainSingle().Subject;
        record.Transport.Should().Be(CommunicationMethodFacade.FallbackTransport);
        record.Outcome.Should().Be(CkMethodErrorCodes.MethodForbidden);
        record.MethodId.Should().Be(methodId);
    }

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task ATokenWithoutATenant_IsRefused_OnBothPaths(string endpoint, string methodId)
    {
        foreach (var roles in new[] { new[] { CommunicationManagement }, ["Development"] })
        {
            var result = await CallAsync(endpoint, Caller(roles, tenant: null));

            StatusOf(result).Should().Be(StatusCodes.Status403Forbidden, $"{endpoint} ({methodId})");
        }

        AssertNothingWasCalled();
    }

    [Theory]
    [MemberData(nameof(Endpoints))]
    public void TheRestPolicyOfEveryFacadeEndpoint_IsUnchanged_TheScopeOfTheApi(string endpoint, string methodId)
    {
        // A caller without the octo_api scope is refused by the REST policy before the action runs: the facade changes no
        // policy (the endpoints keep TenantCommunicationApiReadWritePolicy, execute keeps the read-only one).
        var action = new[] { typeof(AdapterController), typeof(AdapterPoolController), typeof(PipelineController),
                typeof(DataFlowController), typeof(WorkloadController) }
            .Select(t => t.GetMethod(endpoint)).Single(m => m != null)!;
        var policy = action.GetCustomAttributes<AuthorizeAttribute>().Select(a => a.Policy).Single(p => p != null);
        policy.Should().Be(endpoint == nameof(PipelineController.ExecutePipeline)
            ? Constants.TenantCommunicationApiReadOnlyPolicy
            : Constants.TenantCommunicationApiReadWritePolicy, methodId);
    }

    [Fact]
    public async Task WithTheHostSwitchedOff_EveryEndpointRunsOnTheGuardedDirectPath()
    {
        var off = new CommunicationMethodFacade(_provider!,
            _provider!.GetRequiredService<Meshmakers.Octo.Services.Methods.Authorization.ICkMethodAuthorizationDefinitionSource>(),
            _provider!.GetRequiredService<Meshmakers.Octo.Services.Methods.Authorization.ICkMethodAuthorizationEvaluator>(),
            _audit, Microsoft.Extensions.Options.Options.Create(new CommunicationMethodsOptions { Enabled = false }),
            Microsoft.Extensions.Options.Options.Create(new Meshmakers.Octo.Services.Infrastructure.Configuration.TenantAuthorizationOptions()),
            _lifetime, NullLogger<CommunicationMethodFacade>.Instance);
        var result = await off.ServeAsync<IActionResult>(TenantId,
            new CommunicationMethodCall(CommunicationRestFacade.DataFlowCkTypeId,
                SystemCommunicationCkIds.DataFlowDeployMethodId, "test") { Parameters = new DataFlowDeployParameters() },
            Caller([CommunicationManagement]), null, _ => throw new InvalidOperationException("no method call"),
            () => Task.FromResult<IActionResult>(new NoContentResult()), CommunicationRestFacade.FailureOf);

        result.Should().BeOfType<NoContentResult>();
        _audit.Records.Should().ContainSingle().Which.Transport.Should().Be(CommunicationMethodFacade.FallbackTransport);
    }

    // ------------------------------------------------------------------ behaviour of single endpoints

    [Fact]
    public async Task Execute_ReturnsTheExecutionIdAtOnce_AndTheRunIsNotTiedToTheRequest()
    {
        var pipeline = ArrangePipeline();
        var execution = new PipelineExecutionDataDto { Id = Guid.NewGuid(), DateTime = DateTime.UtcNow };
        _trigger.StartExecutePipelineAsync(TenantId, pipeline, Arg.Any<string?>(), Arg.Any<bool>(),
                Arg.Any<ExecutePipelineCaller?>(), Arg.Any<string?>(), Arg.Any<RtPipelineTriggerTypeEnum>(),
                Arg.Any<ExecutePipelineMethodInvocation?>())
            .Returns(execution);
        var controller = Controller(new PipelineController(NullLogger<PipelineController>.Instance, _trigger,
            _adapterService, _repository, _events), Caller([CommunicationManagement]), "{\"a\":1}", "the-bearer");
        // the client is gone before the call: the method still runs (host lifetime, not the request)
        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();
        controller.HttpContext.RequestAborted = aborted.Token;

        var result = await controller.ExecutePipeline(pipeline, isDryRun: true);

        var body = ((OkObjectResult)result).Value.Should().BeOfType<PipelineExecutionDataDto>().Subject;
        body.Id.Should().Be(execution.Id);
        var call = _trigger.ReceivedCalls().Single();
        call.GetArguments()[2].Should().Be("{\"a\":1}");
        call.GetArguments()[3].Should().Be(true);
        call.GetArguments()[5].Should().Be("the-bearer", "the caller token is forwarded for delegation (AB#5031)");
        _audit.Records.Should().ContainSingle().Which.Transport.Should().Be("in-process");
    }

    [Fact]
    public async Task DeployUpdate_ASlowPush_IsAnsweredLikeRest_WithinTheMethodTimeout()
    {
        var adapter = ArrangeAdapter();
        _adapterService.DeployAdapterConfigurationAsync(TenantId, adapter.ToRtEntityId())
            .Returns(async _ => await Task.Delay(TimeSpan.FromSeconds(2)));
        var timeouts = _provider!.GetRequiredService<ICkMethodTimeoutResolver>();

        var result = await Adapter(Caller([CommunicationManagement])).DeployUpdate(adapter.ToRtEntityId().ToString());

        result.Should().BeOfType<NoContentResult>();
        timeouts.GetTimeout(TenantId, CommunicationRestFacade.AdapterCkTypeId,
                SystemCommunicationCkIds.AdapterDeployUpdateMethodId)
            .Should().Be(TimeSpan.FromSeconds(90), "the REST call waits as long as the method may (adapter.yaml)");
        timeouts.GetTimeout(TenantId, CommunicationRestFacade.DeployableWorkloadCkTypeId,
                SystemCommunicationCkIds.DeployableWorkloadWakeMethodId)
            .Should().Be(TimeSpan.FromSeconds(90));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Move_APartialFailure_AnswersPerItemAsBefore(bool withRole)
    {
        var target = new RtEntityId(SystemCommunicationCkIds.RtCkAdapterTypeId, OctoObjectId.GenerateNewId());
        var old = new RtEntityId(SystemCommunicationCkIds.RtCkAdapterTypeId, OctoObjectId.GenerateNewId());
        var moved = OctoObjectId.GenerateNewId();
        var failing = OctoObjectId.GenerateNewId();
        _repository.MovePipelineToAdapterAsync(TenantId, moved, target.RtId)
            .Returns(new PipelineMoveResult(moved, old, target));
        _repository.MovePipelineToAdapterAsync(TenantId, failing, target.RtId)
            .Returns<PipelineMoveResult>(_ => throw CommunicationRepositoryFailure("pipeline missing"));

        var result = await Pipeline(Caller(withRole ? [CommunicationManagement] : ["Development"]))
            .MovePipelinesToAdapter(new MovePipelinesToAdapterRequestDto(
                [moved.ToString(), "not-an-id", failing.ToString()], target.RtId.ToString(), false));

        var items = ((MovePipelinesToAdapterResponseDto)((OkObjectResult)result).Value!).Results;
        items.Select(i => i.Success).Should().Equal(true, false, false);
        items[0].NewAdapterRtId.Should().Be(target.RtId.ToString());
        items[1].ErrorMessage.Should().Contain("is not a valid id");
        items[2].ErrorMessage.Should().Contain("pipeline missing");
        _audit.Records.Should().HaveCount(3, "one audit event per pipeline of the list");
    }

    [Theory]
    [InlineData(true, StatusCodes.Status422UnprocessableEntity)]
    [InlineData(false, StatusCodes.Status404NotFound)]
    public async Task DataFlowDeploy_AFailure_KeepsTheRestStatusCode(bool rejectedByAdapter, int expected)
    {
        var dataFlow = OctoObjectId.GenerateNewId();
        _adapterService.DeployDataFlowAsync(TenantId, dataFlow).Returns<Task>(_ => throw (rejectedByAdapter
            ? HubRejection("definition rejected")
            : AdapterServiceException.AdapterNotLoaded(TenantId,
                new RtEntityId(SystemCommunicationCkIds.RtCkAdapterTypeId, OctoObjectId.GenerateNewId()))));

        var viaMethod = await DataFlow(Caller([CommunicationManagement])).DeployDataFlow(dataFlow);
        var direct = await DataFlow(Caller(["Development"])).DeployDataFlow(dataFlow);

        StatusOf(viaMethod).Should().Be(expected);
        StatusOf(direct).Should().Be(expected);
    }

    [Fact]
    public async Task SetChartVersion_AnInvalidVersion_AnswersTheRestText_OnBothPaths()
    {
        var workload = OctoObjectId.GenerateNewId();

        var viaMethod = await Workload(Caller([CommunicationManagement]))
            .UpdateChartVersion(workload, new UpdateChartVersionDto("not-semver"));
        var direct = await Workload(Caller(["Development"]))
            .UpdateChartVersion(workload, new UpdateChartVersionDto("not-semver"));

        StatusOf(viaMethod).Should().Be(StatusCodes.Status400BadRequest);
        ((ErrorResponse)((ObjectResult)viaMethod).Value!).ErrorMessage.Should()
            .Be(((ErrorResponse)((ObjectResult)direct).Value!).ErrorMessage);
        await _repository.DidNotReceiveWithAnyArgs().UpdateWorkloadChartVersionAsync(default!, default, default!);
    }

    [Fact]
    public async Task Rotate_TheAnswerCarriesNoSecret_OnTheMethodPath()
    {
        var adapter = ArrangeAdapter();
        _provisioning.RotateAdapterSecretAsync(TenantId, adapter)
            .Returns(new PipelineServiceAccountRotationResult("octo-pipeline-sa-z", "pipeline-service-account-z",
                WasCreated: false));

        var result = await Adapter(Caller([CommunicationManagement])).RotateServiceAccountSecret(adapter.RtId.ToString(),
            _provisioning, _events);

        var dto = ((OkObjectResult)result).Value.Should().BeOfType<RotateServiceAccountSecretResultDto>().Subject;
        dto.ClientId.Should().Be("octo-pipeline-sa-z");
        dto.RequiresPipelineRedeploy.Should().BeTrue();
        typeof(RotateServiceAccountSecretResultDto).GetProperties().Select(p => p.Name).Should()
            .NotContain(n => n.Contains("Secret", StringComparison.OrdinalIgnoreCase) && n != "RequiresPipelineRedeploy");
    }

    // ------------------------------------------------------------------ calls

    private async Task<IActionResult> CallAsync(string endpoint, ClaimsPrincipal user)
    {
        var adapter = ArrangeAdapter();
        var workload = OctoObjectId.GenerateNewId();
        var pool = OctoObjectId.GenerateNewId();
        var pipeline = ArrangePipeline();
        var dataFlow = OctoObjectId.GenerateNewId();
        _repository.GetWorkloadByRtIdAsync(TenantId, workload)
            .Returns(new RtApplication { RtId = workload, CkTypeId = SystemCommunicationCkIds.RtCkApplicationTypeId });
        _repository.UpdateWorkloadChartVersionAsync(TenantId, workload, "1.2.3").Returns("1.0.0");
        _provisioning.ReconcileAdapterAsync(TenantId, adapter, Arg.Any<ServiceAccountReconcileContext>())
            .Returns(new ServiceAccountReconcileResult(PipelineServiceAccountProvisioningOutcome.Repaired,
                "octo-pipeline-sa-x", "pipeline-service-account-x", RoleChangesSkipped: true));
        _provisioning.RotateAdapterSecretAsync(TenantId, adapter)
            .Returns(new PipelineServiceAccountRotationResult("octo-pipeline-sa-y", "pipeline-service-account-y",
                WasCreated: false));
        _mirrors.ProvisionForBorrowerAsync(TenantId, Arg.Any<CancellationToken>())
            .Returns(new AdapterPoolMirrorSyncResult(1, 2, 0, 0));
        _mirrors.ProvisionForLenderAsync(TenantId, Arg.Any<CancellationToken>())
            .Returns(new AdapterPoolMirrorSyncResult(1, 0, 0, 0));
        _leases.GrantLeaseAsync(TenantId, pool, Arg.Any<LeaseRequest>(), Arg.Any<CancellationToken>())
            .Returns(new LeaseGrantResult(true, "lease-1", "member-1", null));
        _scheduler.CancelQueuedExecutionAsync(TenantId, pool, "exec-1", Arg.Any<CancellationToken>())
            .Returns(QueueCancellationResult.Cancelled);
        _trigger.StartExecutePipelineAsync(TenantId, pipeline, Arg.Any<string?>(), Arg.Any<bool>(),
                Arg.Any<ExecutePipelineCaller?>(), Arg.Any<string?>(), Arg.Any<RtPipelineTriggerTypeEnum>(),
                Arg.Any<ExecutePipelineMethodInvocation?>())
            .Returns(new PipelineExecutionDataDto { Id = Guid.NewGuid(), DateTime = DateTime.UtcNow });
        var target = new RtEntityId(SystemCommunicationCkIds.RtCkAdapterTypeId, adapter.RtId);
        _repository.MovePipelineToAdapterAsync(TenantId, pipeline, adapter.RtId)
            .Returns(new PipelineMoveResult(pipeline, target, target));
        ClearCalls();

        return endpoint switch
        {
            nameof(AdapterController.DeployUpdate) => await Adapter(user).DeployUpdate(adapter.ToRtEntityId().ToString()),
            nameof(AdapterController.Wake) => await Adapter(user).Wake(workload.ToString(), _lifecycle),
            nameof(AdapterController.ReconcileServiceAccount) => await Adapter(user)
                .ReconcileServiceAccount(adapter.RtId.ToString(), _provisioning, _events),
            nameof(AdapterController.RotateServiceAccountSecret) => await Adapter(user)
                .RotateServiceAccountSecret(adapter.RtId.ToString(), _provisioning, _events),
            nameof(AdapterPoolController.RefreshMirrorsAsync) => await Pool(user).RefreshMirrorsAsync(),
            nameof(AdapterPoolController.PublishMirrorsAsync) => await Pool(user).PublishMirrorsAsync(),
            nameof(AdapterPoolController.GrantLeaseAsync) => await Pool(user).GrantLeaseAsync(pool,
                new GrantAdapterPoolLeaseRequestDto("borrower", OctoObjectId.GenerateNewId().ToString(), "exec-1", 120)),
            nameof(AdapterPoolController.CancelQueuedExecutionAsync) => await Pool(user)
                .CancelQueuedExecutionAsync(pool, "exec-1"),
            nameof(PipelineController.DeployPipeline) => await Pipeline(user, "pipelines: []")
                .DeployPipeline(adapter.ToRtEntityId().ToString(),
                    new RtEntityId(SystemCommunicationCkIds.RtCkPipelineTypeId, pipeline).ToString()),
            nameof(PipelineController.ExecutePipeline) => await Pipeline(user, "{}").ExecutePipeline(pipeline),
            nameof(PipelineController.MovePipelinesToAdapter) => await Pipeline(user).MovePipelinesToAdapter(
                new MovePipelinesToAdapterRequestDto([pipeline.ToString()], adapter.RtId.ToString(), false)),
            nameof(PipelineController.SetPipelineDebugging) => await Pipeline(user)
                .SetPipelineDebugging(pipeline, new SetPipelineDebugRequestDto(true)),
            nameof(DataFlowController.DeployDataFlow) => await DataFlow(user).DeployDataFlow(dataFlow),
            nameof(DataFlowController.UndeployDataFlow) => await DataFlow(user).UndeployDataFlow(dataFlow),
            nameof(WorkloadController.UpdateChartVersion) => await Workload(user)
                .UpdateChartVersion(workload, new UpdateChartVersionDto("1.2.3")),
            _ => throw new ArgumentOutOfRangeException(nameof(endpoint), endpoint, null)
        };
    }

    /// <summary>How often the operation service of the endpoint was called (the lookups do not count).</summary>
    private int ServiceCalls(string endpoint)
    {
        var (service, name) = endpoint switch
        {
            nameof(AdapterController.DeployUpdate) => ((object)_adapterService, nameof(IAdapterService.DeployAdapterConfigurationAsync)),
            nameof(AdapterController.Wake) => (_lifecycle, nameof(IWorkloadLifecycleService.EnsureWorkloadRunningAsync)),
            nameof(AdapterController.ReconcileServiceAccount) => (_provisioning, nameof(IPipelineServiceAccountProvisioningService.ReconcileAdapterAsync)),
            nameof(AdapterController.RotateServiceAccountSecret) => (_provisioning, nameof(IPipelineServiceAccountProvisioningService.RotateAdapterSecretAsync)),
            nameof(AdapterPoolController.RefreshMirrorsAsync) => (_mirrors, nameof(IAdapterPoolMirrorProvisioningService.ProvisionForBorrowerAsync)),
            nameof(AdapterPoolController.PublishMirrorsAsync) => (_mirrors, nameof(IAdapterPoolMirrorProvisioningService.ProvisionForLenderAsync)),
            nameof(AdapterPoolController.GrantLeaseAsync) => (_leases, nameof(ILeaseService.GrantLeaseAsync)),
            nameof(AdapterPoolController.CancelQueuedExecutionAsync) => (_scheduler, nameof(ILeaseSchedulerService.CancelQueuedExecutionAsync)),
            nameof(PipelineController.DeployPipeline) => (_adapterService, nameof(IAdapterService.DeployPipelineAsync)),
            nameof(PipelineController.ExecutePipeline) => (_trigger, nameof(ITriggerManagementService.StartExecutePipelineAsync)),
            nameof(PipelineController.MovePipelinesToAdapter) => (_repository, nameof(ICommunicationRepository.MovePipelineToAdapterAsync)),
            nameof(PipelineController.SetPipelineDebugging) => (_adapterService, nameof(IAdapterService.SetPipelineDebuggingAsync)),
            nameof(DataFlowController.DeployDataFlow) => (_adapterService, nameof(IAdapterService.DeployDataFlowAsync)),
            nameof(DataFlowController.UndeployDataFlow) => (_adapterService, nameof(IAdapterService.UndeployDataFlowAsync)),
            nameof(WorkloadController.UpdateChartVersion) => (_repository, nameof(ICommunicationRepository.UpdateWorkloadChartVersionAsync)),
            _ => throw new ArgumentOutOfRangeException(nameof(endpoint), endpoint, null)
        };
        return service.ReceivedCalls().Count(c => c.GetMethodInfo().Name == name);
    }

    private void ClearCalls()
    {
        foreach (var service in Services())
        {
            service.ClearReceivedCalls();
        }
    }

    private object[] Services() =>
        [_repository, _adapterService, _lifecycle, _provisioning, _events, _mirrors, _leases, _scheduler, _trigger];

    private void AssertNothingWasCalled()
    {
        foreach (var service in Services())
        {
            service.ReceivedCalls().Should().BeEmpty($"{service.GetType().Name} must not be called for a refused caller");
        }
    }

    private RtAdapter ArrangeAdapter()
    {
        var adapter = new RtAdapter
        {
            RtId = OctoObjectId.GenerateNewId(),
            CkTypeId = SystemCommunicationCkIds.RtCkAdapterTypeId,
            Name = "mesh-adapter"
        };
        _repository.GetAdaptersAsync(TenantId).Returns([adapter]);
        return adapter;
    }

    private OctoObjectId ArrangePipeline()
    {
        var pipeline = new RtPipeline
        {
            RtId = OctoObjectId.GenerateNewId(),
            CkTypeId = SystemCommunicationCkIds.RtCkPipelineTypeId
        };
        _repository.GetPipelineAsync(TenantId, pipeline.ToRtEntityId()).Returns(pipeline);
        return pipeline.RtId;
    }

    private ClaimsPrincipal Caller(string[] roles, string? tenant = "")
    {
        var claims = new List<Claim> { new("sub", Subject), new("scope", "octo_api"), new("name", "Facade User") };
        claims.AddRange(roles.Select(r => new Claim("role", r)));
        if (tenant != null)
        {
            claims.Add(new Claim("tenant_id", tenant.Length == 0 ? TenantId : tenant));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer", "name", "role"));
    }

    private T Controller<T>(T controller, ClaimsPrincipal user, string? body = null, string? bearer = null)
        where T : ControllerBase
    {
        var context = new DefaultHttpContext
        {
            User = user,
            RequestServices = _provider!.CreateScope().ServiceProvider
        };
        context.Request.RouteValues["tenantId"] = TenantId;
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body ?? string.Empty));
        if (bearer != null)
        {
            context.Request.Headers.Authorization = $"Bearer {bearer}";
        }

        controller.ControllerContext = new ControllerContext { HttpContext = context };
        return controller;
    }

    private AdapterController Adapter(ClaimsPrincipal user) => Controller(new AdapterController(
        NullLogger<AdapterController>.Instance, _repository, _adapterService, Substitute.For<ITenantLendingScopeResolver>()), user);

    private AdapterPoolController Pool(ClaimsPrincipal user) => Controller(new AdapterPoolController(
        Substitute.For<IAdapterPoolConnectionManager>(), _scheduler, _leases, _mirrors, _repository,
        Substitute.For<ITenantLendingScopeResolver>(), NullLogger<AdapterPoolController>.Instance), user);

    private PipelineController Pipeline(ClaimsPrincipal user, string? body = null) => Controller(new PipelineController(
        NullLogger<PipelineController>.Instance, _trigger, _adapterService, _repository, _events), user, body);

    private DataFlowController DataFlow(ClaimsPrincipal user) => Controller(new DataFlowController(
        NullLogger<DataFlowController>.Instance, _adapterService, Substitute.For<IPipelineExecutionService>()), user);

    private WorkloadController Workload(ClaimsPrincipal user) => Controller(new WorkloadController(
        NullLogger<WorkloadController>.Instance, _repository, _events), user);

    private static int StatusOf(IActionResult result) =>
        result is IStatusCodeActionResult { StatusCode: { } status } ? status : StatusCodes.Status200OK;

    /// <summary>The adapter's rejection (the exception has private constructors; the service creates it).</summary>
    private static Exception HubRejection(string message) =>
        (Exception)Activator.CreateInstance(typeof(AdapterHubCallbackException),
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null, [message], null)!;

    private static Exception CommunicationRepositoryFailure(string message) =>
        (Exception)Activator.CreateInstance(typeof(CommunicationRepositoryException),
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null, [message], null)!;

    private sealed class CapturingAudit : ICkMethodAuditRecorder
    {
        private readonly ConcurrentQueue<CkMethodAuditRecord> _records = new();

        public IReadOnlyList<CkMethodAuditRecord> Records => _records.ToList();

        public Task RecordAsync(CkMethodAuditRecord record, CancellationToken cancellationToken = default)
        {
            _records.Enqueue(record);
            return Task.CompletedTask;
        }
    }

    private sealed class CapturingLogs : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _entries = new();

        public string All => string.Join("\n", _entries);

        public ILogger CreateLogger(string categoryName) => new Sink(_entries);

        public void Dispose()
        {
        }

        private sealed class Sink(ConcurrentQueue<string> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                entries.Enqueue($"[{logLevel}] {formatter(state, exception)}");
        }
    }
}
