using System.Diagnostics;
using FluentAssertions;
using Meshmakers.Octo.Backend.CommunicationControllerServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Methods;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.Communication.Contracts.MessageObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Meshmakers.Octo.Services.Methods.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Commands;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Messages;
using NSubstitute;
using Xunit;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.IntegrationTests.Methods;

/// <summary>
///     AB#6879 end to end at the boundary of the controller: a method declared <c>handler: pipeline</c> is invoked
///     over the real <see cref="PipelineMethodInvoker" />, the pipeline is found by its well-known name in a real
///     MongoDB tenant, started by the real <see cref="TriggerManagementService" /> and its execution outcome is read
///     from the real execution store.
/// </summary>
/// <remarks>
///     The pipeline runtime lives in the adapter (octo-communication-sdk, octo-mesh-adapter) and is not part of this
///     service. The adapter is played by a stub of the controller-to-adapter command (the
///     <c>FromExecutePipelineCommand</c> endpoint): it receives the real <see cref="ExecutePipelineRequest" /> and
///     reports the execution into the store exactly as the adapter hub does (execution created Running, then the end
///     with the <c>OutputData</c> that <c>SetPipelineExecutionResult@1</c> produced).
/// </remarks>
[Collection("CommunicationController")]
public class PipelineMethodHandlerTests(CommunicationControllerFixture fixture)
{
    /// <summary>The pipeline of the documented sample (the guide "Pipeline Method Handlers").</summary>
    private static readonly string SamplePipeline = File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
        "Methods", "SamplePipelines", "Device.UpdateFirmware-1.pipeline.yaml"));

    private sealed class Scenario
    {
        public required string MethodId { get; init; }
        public required PipelineMethodDefinition Method { get; init; }
        public required RtEntityId PipelineEntityId { get; init; }
        public required RtEntityId AdapterEntityId { get; init; }
        public required RtEntityId DataFlowEntityId { get; init; }
        public ExecutePipelineRequest? ReceivedRequest { get; set; }
        public int StartCount { get; set; }
    }

    private static string NewMethodId() => $"Acme.Assets/Device.Update{Guid.NewGuid():N}-1";

    private PipelineMethodInvoker CreateInvoker(PipelineMethodDefinition method, out CapturingLog log)
    {
        var catalog = Substitute.For<IPipelineMethodCatalog>();
        catalog.IsTenantEnabled(fixture.TestTenantId).Returns(true);
        catalog.Find(fixture.TestTenantId, method.MethodId).Returns(method);
        var options = Microsoft.Extensions.Options.Options.Create(new PipelineMethodOptions
        {
            PollInterval = TimeSpan.FromMilliseconds(20), MaxPollInterval = TimeSpan.FromMilliseconds(100)
        });
        log = new CapturingLog();
        // The real trigger management service over the real repository. The fixture registers it, but not every
        // dependency of it (lease scheduler, encryption), so it is assembled here; the command client to the
        // adapter is the fixture's stub, which ArrangeAdapter turns into the adapter.
        var trigger = new TriggerManagementService(
            NullLogger<TriggerManagementService>.Instance,
            fixture.GetService<ICommunicationRepository>(),
            Substitute.For<ICommandClient<RemoveRecurringJobsByScheduleGroupRequest>>(),
            fixture.GetService<IRoutedCommandClient<ExecutePipelineRequest>>(),
            Substitute.For<IDistributionEventHubService>(),
            fixture.GetService<ICommunicationEventService>(),
            Substitute.For<IWorkloadLifecycleService>(),
            Substitute.For<ILifecycleConfigurationService>(),
            Substitute.For<ILeaseSchedulerWakeSignal>(),
            fixture.GetService<IExecutionEnvelope>());
        var services = new ServiceCollection().AddSingleton<ITriggerManagementService>(trigger).BuildServiceProvider();
        return new PipelineMethodInvoker(catalog, fixture.GetService<ICommunicationRepository>(),
            services, new PipelineMethodHandlerInstance(options, TimeProvider.System), options,
            TimeProvider.System, TokenValidator, log);
    }

    private sealed class CapturingLog : ILogger<PipelineMethodInvoker>
    {
        private readonly List<string> _entries = [];

        public string All
        {
            get
            {
                lock (_entries)
                {
                    return string.Join("\n", _entries);
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_entries)
            {
                _entries.Add(formatter(state, exception) + exception);
            }
        }
    }

    private static PipelineMethodDefinition MethodDefinition(string tenantId, string methodId,
        bool sensitive = false, int timeoutSeconds = 5) =>
        new(tenantId, methodId, new CkMethodDto
        {
            MethodId = methodId[(methodId.LastIndexOf('.') + 1)..],
            Parameters =
            [
                new CkMethodParameterDto { Name = "version", ValueType = AttributeValueTypesDto.String },
                ..sensitive
                    ? new[]
                    {
                        new CkMethodParameterDto
                            { Name = "signingKey", ValueType = AttributeValueTypesDto.String, Sensitive = true }
                    }
                    : []
            ],
            Result = new CkMethodResultDto { ValueType = AttributeValueTypesDto.Integer },
            Errors = [new CkMethodErrorDto { Code = "FIRMWARE_LOCKED" }],
            Execution = new CkMethodExecutionDto { TimeoutSeconds = timeoutSeconds, Handler = CkMethodHandlers.Pipeline }
        });

    /// <summary>
    ///     The real caller token validator in its test configuration (<c>DisableValidation</c>, allowed in Development only):
    ///     issuer and signature are not checked, scope and tenant rules still apply. The refusal cases with real signatures
    ///     are in the unit tests (<c>PipelineMethodCallerTokenTests</c>); there is no identity service here.
    /// </summary>
    private static readonly ICkMethodCallerTokenValidator TokenValidator = new ServiceCollection()
        .AddLogging()
        .AddSingleton(DevelopmentEnvironment())
        .AddSingleton<Microsoft.Extensions.Configuration.IConfiguration>(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build())
        .AddCkMethodCallerTokenValidation(o => o.DisableValidation = true)
        .BuildServiceProvider()
        .GetRequiredService<ICkMethodCallerTokenValidator>();

    private static Microsoft.Extensions.Hosting.IHostEnvironment DevelopmentEnvironment()
    {
        var environment = Substitute.For<Microsoft.Extensions.Hosting.IHostEnvironment>();
        environment.EnvironmentName.Returns(Microsoft.Extensions.Hosting.Environments.Development);
        return environment;
    }

    /// <summary>An unsigned token with the claims of a user of <paramref name="tenantId" />.</summary>
    private static string CallerToken(string tenantId)
    {
        static string B64(string json) =>
            Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        return B64("{\"alg\":\"none\"}") + "." +
               B64($"{{\"sub\":\"token-subject\",\"client_id\":\"studio\",\"tenant_id\":\"{tenantId}\"," +
                   "\"scope\":\"octo_api\",\"role\":[\"Reporting\"]}") + ".";
    }

    private static CkMethodRequest Request(string methodId, string tenantId, int deadlineSeconds = 10) =>
        new(tenantId)
        {
            InvocationId = Guid.NewGuid(),
            MethodId = methodId,
            CkTypeId = "Acme.Assets/Device",
            TargetRtId = OctoObjectId.GenerateNewId().ToString(),
            ParametersJson = "{\"version\":\"1.2.3\"}",
            DeadlineUtc = DateTime.UtcNow.AddSeconds(deadlineSeconds),
            CallerAccessToken = CallerToken(tenantId),
            Caller = new CkMethodCaller { SubjectId = "user-42", TenantId = tenantId, Roles = ["TenantAdmin"] }
        };

    /// <summary>
    ///     The adapter: answers the execute command and reports the execution. <paramref name="finalStatus" /> null
    ///     means the pipeline never ends.
    /// </summary>
    private void ArrangeAdapter(Scenario scenario, RtPipelineExecutionStatusEnum? finalStatus, string? output,
        string? error = null)
    {
        var client = fixture.GetService<IRoutedCommandClient<ExecutePipelineRequest>>();
        var repository = fixture.GetService<ICommunicationRepository>();
        client.ClearReceivedCalls();
        client.GetResponse<ExecutePipelineResponse>(Arg.Any<string>(), Arg.Any<ExecutePipelineRequest>(),
                Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>())
            .Returns(call =>
            {
                scenario.ReceivedRequest = call.ArgAt<ExecutePipelineRequest>(1);
                scenario.StartCount++;
                var executionId = Guid.NewGuid();
                _ = Task.Run(async () =>
                {
                    await Task.Delay(50);
                    await repository.CreatePipelineExecutionAsync(fixture.TestTenantId,
                        new RtPipelineExecution
                        {
                            RtId = OctoObjectId.GenerateNewId(),
                            CkTypeId = SystemCommunicationCkIds.RtCkPipelineExecutionTypeId,
                            ExecutionId = executionId.ToString(),
                            Status = RtPipelineExecutionStatusEnum.Running,
                            TriggerType = RtPipelineTriggerTypeEnum.Manual,
                            StartedAt = DateTime.UtcNow,
                            InputData = scenario.ReceivedRequest!.PipelineInput
                        }, scenario.PipelineEntityId, scenario.AdapterEntityId);
                    if (finalStatus is { } status)
                    {
                        await Task.Delay(150);
                        await repository.UpdatePipelineExecutionAsync(fixture.TestTenantId, executionId.ToString(),
                            status, DateTime.UtcNow, 150, error, output);
                    }
                });
                return Task.FromResult(new ExecutePipelineResponse(true, null, executionId, DateTime.UtcNow));
            });
    }

    [Fact]
    public async Task ThePipelineResultComesBackInTheSuccessEnvelopeAndThePipelineRanAsTheCaller()
    {
        var scenario = await CreateScenarioAsync();
        try
        {
            ArrangeAdapter(scenario, RtPipelineExecutionStatusEnum.Completed, "{\"value\": 7}");
            var invoker = CreateInvoker(scenario.Method, out var log0);
            var request = Request(scenario.MethodId, fixture.TestTenantId);

            var response = await invoker.InvokeAsync(request, TestContext.Current.CancellationToken);

            response.Success.Should().BeTrue("the log says: {0}", log0.All);
            response.ResultJson.Should().Be("{\"value\":7}");
            scenario.StartCount.Should().Be(1);

            // the real controller-to-adapter command carried the invoker and the method input
            var received = scenario.ReceivedRequest!;
            received.TenantId.Should().Be(fixture.TestTenantId);
            // the caller is the validated token's, not the Caller member of the request (user-42, TenantAdmin)
            received.Caller!.SubjectId.Should().Be("token-subject");
            received.Caller.Roles.Should().Contain("Reporting").And.NotContain("TenantAdmin");
            received.Caller.TrustLevel.Should().Be(2);
            received.CallerAccessToken.Should().Be(request.CallerAccessToken);
            received.IsDryRun.Should().BeFalse();
            received.PipelineInput.Should().Contain("\"version\":\"1.2.3\"").And.Contain(request.TargetRtId!);
        }
        finally
        {
            await CleanupAsync(scenario);
        }
    }

    [Fact]
    public async Task AForgedCallerWithoutAValidToken_IsForbidden_AndTheAdapterIsNeverAsked()
    {
        var scenario = await CreateScenarioAsync();
        try
        {
            ArrangeAdapter(scenario, RtPipelineExecutionStatusEnum.Completed, "{\"value\": 7}");
            var invoker = CreateInvoker(scenario.Method, out _);
            var request = Request(scenario.MethodId, fixture.TestTenantId) with { CallerAccessToken = null };

            var response = await invoker.InvokeAsync(request, TestContext.Current.CancellationToken);

            response.Success.Should().BeFalse();
            response.Error!.Code.Should().Be(CkMethodErrorCodes.MethodForbidden);
            response.Error.Parameters["reason"].Should().Be(CkMethodTokenReasons.TokenMissing);
            scenario.StartCount.Should().Be(0);
        }
        finally
        {
            await CleanupAsync(scenario);
        }
    }

    [Fact]
    public async Task ADeclaredErrorSetByThePipelineIsTheMethodError()
    {
        var scenario = await CreateScenarioAsync();
        try
        {
            ArrangeAdapter(scenario, RtPipelineExecutionStatusEnum.Completed,
                "{\"error\": {\"code\": \"FIRMWARE_LOCKED\", \"parameters\": {\"until\": \"2026-12-31\"}}}");
            var invoker = CreateInvoker(scenario.Method, out _);

            var response = await invoker.InvokeAsync(Request(scenario.MethodId, fixture.TestTenantId), TestContext.Current.CancellationToken);

            response.Success.Should().BeFalse();
            response.Error!.Code.Should().Be("FIRMWARE_LOCKED");
            response.Error.Parameters["until"].Should().Be("2026-12-31");
        }
        finally
        {
            await CleanupAsync(scenario);
        }
    }

    [Fact]
    public async Task AFailingPipelineIsHandlerFailedAndItsErrorMessageNeverLeavesTheService()
    {
        var scenario = await CreateScenarioAsync();
        try
        {
            ArrangeAdapter(scenario, RtPipelineExecutionStatusEnum.Failed, null, "Node ApplyChanges failed: boom");
            var invoker = CreateInvoker(scenario.Method, out var log);
            var request = Request(scenario.MethodId, fixture.TestTenantId);

            var response = await invoker.InvokeAsync(request, TestContext.Current.CancellationToken);

            response.Error!.Code.Should().Be(CkMethodErrorCodes.MethodHandlerFailed);
            response.Error.Parameters["correlationId"].Should().Be(request.InvocationId.ToString());
            response.Error.Parameters.Values.Should().NotContain(v => v.Contains("boom"));
            log.All.Should().Contain("boom").And.NotContain(request.CallerAccessToken!);
        }
        finally
        {
            await CleanupAsync(scenario);
        }
    }

    [Fact]
    public async Task APipelineThatNeverEndsIsAMethodTimeout()
    {
        var scenario = await CreateScenarioAsync();
        try
        {
            ArrangeAdapter(scenario, null, null);
            var invoker = CreateInvoker(scenario.Method, out _);
            var stopwatch = Stopwatch.StartNew();

            var response = await invoker.InvokeAsync(Request(scenario.MethodId, fixture.TestTenantId, 2), TestContext.Current.CancellationToken);

            response.Error!.Code.Should().Be(CkMethodErrorCodes.MethodTimeout);
            stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(6));
        }
        finally
        {
            await CleanupAsync(scenario);
        }
    }

    [Fact]
    public async Task WithoutAPipelineOfThatNameTheAnswerIsHandlerNotAvailableWithinASecond()
    {
        var methodId = NewMethodId();
        var invoker = CreateInvoker(MethodDefinition(fixture.TestTenantId, methodId), out _);
        var stopwatch = Stopwatch.StartNew();

        var response = await invoker.InvokeAsync(Request(methodId, fixture.TestTenantId), TestContext.Current.CancellationToken);

        response.Error!.Code.Should().Be(CkMethodErrorCodes.MethodHandlerNotAvailable);
        response.Error.Parameters["reason"].Should().Be("pipeline-not-deployed");
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task AMethodWithSensitiveParametersIsRefusedAndNoPipelineIsStarted()
    {
        var scenario = await CreateScenarioAsync(sensitive: true);
        try
        {
            ArrangeAdapter(scenario, RtPipelineExecutionStatusEnum.Completed, "{\"value\": 7}");
            var invoker = CreateInvoker(scenario.Method, out var log);
            var request = Request(scenario.MethodId, fixture.TestTenantId) with
            {
                ParametersJson = "{\"version\":\"1\",\"signingKey\":\"TOP-SECRET-KEY-VALUE\"}"
            };

            var response = await invoker.InvokeAsync(request, TestContext.Current.CancellationToken);

            response.Error!.Code.Should().Be(CkMethodErrorCodes.MethodHandlerNotAvailable);
            response.Error.Parameters["reason"].Should().Be("sensitive-parameters-not-supported");
            scenario.StartCount.Should().Be(0);
            log.All.Should().NotContain("TOP-SECRET-KEY-VALUE");
        }
        finally
        {
            await CleanupAsync(scenario);
        }
    }

    [Fact]
    public async Task APipelineWithAResultThatDoesNotMatchTheResultRecordIsHandlerFailed()
    {
        var scenario = await CreateScenarioAsync();
        try
        {
            ArrangeAdapter(scenario, RtPipelineExecutionStatusEnum.Completed, "{\"value\": \"seven\"}");
            var invoker = CreateInvoker(scenario.Method, out _);

            var response = await invoker.InvokeAsync(Request(scenario.MethodId, fixture.TestTenantId), TestContext.Current.CancellationToken);

            response.Success.Should().BeFalse();
            response.Error!.Code.Should().Be(CkMethodErrorCodes.MethodHandlerFailed);
            response.Error.Parameters["field"].Should().Be("value");
        }
        finally
        {
            await CleanupAsync(scenario);
        }
    }

    // ---- data ---------------------------------------------------------------------------------------------------

    private async Task<Scenario> CreateScenarioAsync(bool sensitive = false)
    {
        var methodId = NewMethodId();
        var systemContext = fixture.GetSystemContext();
        var tenantRepository = await systemContext.FindTenantRepositoryAsync(fixture.TestTenantId);

        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();

        var adapter = new RtAdapter
        {
            RtId = OctoObjectId.GenerateNewId(),
            CkTypeId = SystemCommunicationCkIds.RtCkAdapterTypeId,
            Name = $"int-test-adapter-{Guid.NewGuid():N}",
            CommunicationState = RtCommunicationStateEnum.Online,
            DeploymentState = RtDeploymentStateEnum.Deployed,
            ConfigurationState = RtConfigurationStateEnum.Unconfigured
        };
        var dataFlow = new RtDataFlow
        {
            RtId = OctoObjectId.GenerateNewId(),
            CkTypeId = SystemCommunicationCkIds.RtCkDataFlowTypeId,
            Name = $"int-test-dataflow-{Guid.NewGuid():N}"
        };
        var pipeline = new RtPipeline
        {
            RtId = OctoObjectId.GenerateNewId(),
            CkTypeId = SystemCommunicationCkIds.RtCkPipelineTypeId,
            Name = $"int-test-pipeline-{Guid.NewGuid():N}",
            RtWellKnownName = methodId,
            Enabled = true,
            DeploymentState = RtDeploymentStateEnum.Deployed,
            PipelineDefinition = SamplePipeline
        };
        var adapterId = new RtEntityId(SystemCommunicationCkIds.RtCkAdapterTypeId, adapter.RtId);
        var dataFlowId = new RtEntityId(SystemCommunicationCkIds.RtCkDataFlowTypeId, dataFlow.RtId);
        var pipelineId = new RtEntityId(SystemCommunicationCkIds.RtCkPipelineTypeId, pipeline.RtId);

        var result = new OperationResult();
        await tenantRepository.ApplyChangesAsync(session,
            new List<EntityUpdateInfo<RtAdapter>> { EntityUpdateInfo<RtAdapter>.CreateInsert(adapter) }, result);
        await tenantRepository.ApplyChangesAsync(session,
            new List<EntityUpdateInfo<RtDataFlow>> { EntityUpdateInfo<RtDataFlow>.CreateInsert(dataFlow) }, result);
        await tenantRepository.ApplyChangesAsync(session,
            new List<EntityUpdateInfo<RtPipeline>> { EntityUpdateInfo<RtPipeline>.CreateInsert(pipeline) },
            new List<AssociationUpdateInfo>
            {
                AssociationUpdateInfo.CreateInsert(pipelineId, dataFlowId, SystemCkIds.RtCkParentChildRoleId),
                AssociationUpdateInfo.CreateInsert(pipelineId, adapterId, SystemCommunicationCkIds.RtCkExecutesRoleId)
            }, result);
        if (result.HasErrors || result.HasFatalErrors)
        {
            await session.AbortTransactionAsync();
            throw new InvalidOperationException($"Failed to insert the test data: {result.GetMessages()}");
        }

        await session.CommitTransactionAsync();

        return new Scenario
        {
            MethodId = methodId,
            Method = MethodDefinition(fixture.TestTenantId, methodId, sensitive),
            PipelineEntityId = pipelineId,
            AdapterEntityId = adapterId,
            DataFlowEntityId = dataFlowId
        };
    }

    private async Task CleanupAsync(Scenario scenario)
    {
        var systemContext = fixture.GetSystemContext();
        var tenantRepository = await systemContext.FindTenantRepositoryAsync(fixture.TestTenantId);
        await TryDeleteAsync<RtPipeline>(tenantRepository, scenario.PipelineEntityId);
        await TryDeleteAsync<RtDataFlow>(tenantRepository, scenario.DataFlowEntityId);
        await TryDeleteAsync<RtAdapter>(tenantRepository, scenario.AdapterEntityId);
    }

    private static async Task TryDeleteAsync<TEntity>(ITenantRepository tenantRepository, RtEntityId entity)
        where TEntity : RtEntity
    {
        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();
        try
        {
            var operationResult = new OperationResult();
            await tenantRepository.ApplyChangesAsync(session,
                new List<EntityUpdateInfo<TEntity>> { EntityUpdateInfo<TEntity>.CreateDelete(entity) },
                operationResult);
            await session.CommitTransactionAsync();
        }
        catch
        {
            await session.AbortTransactionAsync();
        }
    }
}
