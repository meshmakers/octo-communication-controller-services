using System.Security.Claims;
using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Models;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Options;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Backend.CommunicationControllerServices.TenantApi.v1.Controllers;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services;

/// <summary>
/// AB#5537 / AB#5114 — the tenant-wide impersonation switch: the explicit guard, the per-account
/// fan-out and the data-flow redeploy of switched accounts, plus the endpoint's mapping.
/// </summary>
internal class ServiceAccountImpersonationSwitchServiceTests
{
    private const string TenantId = "tenantId";

    private readonly ICommunicationRepository _repository = Substitute.For<ICommunicationRepository>();

    private readonly IPipelineServiceAccountProvisioningService _provisioning =
        Substitute.For<IPipelineServiceAccountProvisioningService>();

    private readonly IAdapterService _adapterService = Substitute.For<IAdapterService>();

    private ServiceAccountImpersonationSwitchService CreateSut(bool allowSecretClearing)
    {
        return new ServiceAccountImpersonationSwitchService(_repository, _provisioning, _adapterService,
            Options.Create(new ServiceAccountImpersonationOptions { AllowSecretClearing = allowSecretClearing }));
    }

    private static readonly string[] Actors = ["octo-pipeline-sa-adapter"];

    private RtServiceAccountConfiguration ArrangeAccount(ServiceAccountImpersonationSwitchOutcome outcome,
        params OctoObjectId[] pipelineRtIds)
    {
        return ArrangeAccount(outcome, actors: outcome is ServiceAccountImpersonationSwitchOutcome.SwitchedPendingInvalidation
            or ServiceAccountImpersonationSwitchOutcome.WouldSwitch ? Actors : [], pipelineRtIds);
    }

    private RtServiceAccountConfiguration ArrangeAccount(ServiceAccountImpersonationSwitchOutcome outcome,
        IReadOnlyList<string> actors, params OctoObjectId[] pipelineRtIds)
    {
        var account = RtEntityCreator.CreateServiceAccountConfiguration($"sa-{OctoObjectId.GenerateNewId()}");
        _provisioning.SwitchToImpersonationAsync(TenantId, account, Arg.Any<bool>())
            .Returns(new ServiceAccountImpersonationSwitchResult(account.RtId, account.RtWellKnownName!, "client",
                outcome, outcome.ToString(), pipelineRtIds, actors));
        return account;
    }

    private RtDataFlow ArrangeDataFlow(params OctoObjectId[] pipelineRtIds)
    {
        var dataFlow = RtEntityCreator.CreateDataFlow();
        foreach (var pipelineRtId in pipelineRtIds)
        {
            _repository.GetDataFlowByPipelineAsync(TenantId, pipelineRtId).Returns(dataFlow);
        }

        return dataFlow;
    }

    private static ServiceAccountImpersonationSwitchResult For(ServiceAccountImpersonationSwitchReport report,
        RtServiceAccountConfiguration account)
    {
        return report.Accounts.Single(a => a.ConfigurationRtId == account.RtId);
    }

    [Test]
    public async Task Apply_WithGuardOff_IsRefusedBeforeAnyRead()
    {
        var sut = CreateSut(allowSecretClearing: false);

        await Assert.That(async () => { await sut.SwitchTenantAsync(TenantId, apply: true, redeployDataFlows: true); })
            .Throws<ServiceAccountImpersonationSwitchDisabledException>();
        await _repository.DidNotReceiveWithAnyArgs().GetServiceAccountsAsync(default!);
    }

    [Test]
    public async Task DryRun_WithGuardOff_ClassifiesEveryAccountAndRedeploysNothing()
    {
        var a = ArrangeAccount(ServiceAccountImpersonationSwitchOutcome.WouldSwitch, OctoObjectId.GenerateNewId());
        var b = ArrangeAccount(ServiceAccountImpersonationSwitchOutcome.SkippedAdapterOwnIdentity);
        _repository.GetServiceAccountsAsync(TenantId).Returns([a, b]);

        var report = await CreateSut(allowSecretClearing: false)
            .SwitchTenantAsync(TenantId, apply: false, redeployDataFlows: true);

        using var _ = Assert.Multiple();
        await Assert.That(report.Applied).IsFalse();
        await Assert.That(report.Accounts.Count).IsEqualTo(2);
        await _provisioning.Received(2).SwitchToImpersonationAsync(TenantId, Arg.Any<RtServiceAccountConfiguration>(),
            false);
        await _adapterService.DidNotReceiveWithAnyArgs().DeployDataFlowAsync(default!, default);
    }

    [Test]
    public async Task Apply_RedeploysEachDataFlowOnce_InvalidatesOnlyFullyRedeployedAccounts()
    {
        var pipeline1 = OctoObjectId.GenerateNewId();
        var pipeline2 = OctoObjectId.GenerateNewId();
        var pipeline3 = OctoObjectId.GenerateNewId();
        var skippedPipeline = OctoObjectId.GenerateNewId();
        var redeployed = ArrangeAccount(ServiceAccountImpersonationSwitchOutcome.SwitchedPendingInvalidation,
            pipeline1, pipeline2);
        var redeployFailed = ArrangeAccount(ServiceAccountImpersonationSwitchOutcome.SwitchedPendingInvalidation,
            pipeline1, pipeline3);
        var skipped = ArrangeAccount(ServiceAccountImpersonationSwitchOutcome.SkippedNoCapableActor, skippedPipeline);
        _repository.GetServiceAccountsAsync(TenantId).Returns([redeployed, redeployFailed, skipped]);

        var sharedFlow = ArrangeDataFlow(pipeline1, pipeline2);
        var failingFlow = ArrangeDataFlow(pipeline3);
        _adapterService.DeployDataFlowAsync(TenantId, failingFlow.RtId)
            .ThrowsAsync(new InvalidOperationException("adapter offline"));

        var report = await CreateSut(allowSecretClearing: true)
            .SwitchTenantAsync(TenantId, apply: true, redeployDataFlows: true);

        using var _ = Assert.Multiple();
        await Assert.That(report.Applied).IsTrue();
        await _adapterService.Received(1).DeployDataFlowAsync(TenantId, sharedFlow.RtId);
        await Assert.That(report.RedeployedDataFlowRtIds).IsEquivalentTo(new[] { sharedFlow.RtId });
        await Assert.That(report.FailedDataFlowRedeploys.Keys).IsEquivalentTo(new[] { failingFlow.RtId });
        await _repository.DidNotReceive().GetDataFlowByPipelineAsync(TenantId, skippedPipeline);
        // (d) only where EVERY data flow of the account redeployed.
        await _provisioning.Received(1).InvalidateSwitchedSecretAsync(TenantId, redeployed,
            Arg.Is<IReadOnlyList<string>>(a => a.SequenceEqual(Actors)));
        await _provisioning.DidNotReceive().InvalidateSwitchedSecretAsync(TenantId, redeployFailed,
            Arg.Any<IReadOnlyList<string>>());
        await Assert.That(For(report, redeployed).Outcome).IsEqualTo(ServiceAccountImpersonationSwitchOutcome.Switched);
        await Assert.That(For(report, redeployFailed).Outcome)
            .IsEqualTo(ServiceAccountImpersonationSwitchOutcome.SwitchedPendingInvalidation);
        await Assert.That(For(report, redeployFailed).Message).Contains(failingFlow.RtId.ToString());
        await Assert.That(For(report, skipped).Outcome)
            .IsEqualTo(ServiceAccountImpersonationSwitchOutcome.SkippedNoCapableActor);
    }

    [Test]
    public async Task Apply_WithoutRedeploy_LeavesDataFlowsAlone_AndInvalidatesNothing()
    {
        var switched = ArrangeAccount(ServiceAccountImpersonationSwitchOutcome.SwitchedPendingInvalidation,
            OctoObjectId.GenerateNewId());
        _repository.GetServiceAccountsAsync(TenantId).Returns([switched]);

        var report = await CreateSut(allowSecretClearing: true)
            .SwitchTenantAsync(TenantId, apply: true, redeployDataFlows: false);

        using var _ = Assert.Multiple();
        await _adapterService.DidNotReceiveWithAnyArgs().DeployDataFlowAsync(default!, default);
        await _provisioning.DidNotReceiveWithAnyArgs().InvalidateSwitchedSecretAsync(default!, default!, default!);
        await Assert.That(For(report, switched).Outcome)
            .IsEqualTo(ServiceAccountImpersonationSwitchOutcome.SwitchedPendingInvalidation);
        await Assert.That(For(report, switched).Message).Contains("redeployDataFlows=true");
    }

    [Test]
    public async Task Apply_InvalidationFails_StaysPending()
    {
        var pipeline = OctoObjectId.GenerateNewId();
        var switched = ArrangeAccount(ServiceAccountImpersonationSwitchOutcome.SwitchedPendingInvalidation, pipeline);
        _repository.GetServiceAccountsAsync(TenantId).Returns([switched]);
        ArrangeDataFlow(pipeline);
        _provisioning.InvalidateSwitchedSecretAsync(TenantId, switched, Arg.Any<IReadOnlyList<string>>())
            .ThrowsAsync(new TimeoutException("identity down"));

        var report = await CreateSut(allowSecretClearing: true)
            .SwitchTenantAsync(TenantId, apply: true, redeployDataFlows: true);

        using var _ = Assert.Multiple();
        await Assert.That(For(report, switched).Outcome)
            .IsEqualTo(ServiceAccountImpersonationSwitchOutcome.SwitchedPendingInvalidation);
        await Assert.That(For(report, switched).Message).Contains("TimeoutException");
    }

    [Test]
    public async Task Apply_DataFlowCannotBeResolved_DoesNotInvalidate()
    {
        var pipeline = OctoObjectId.GenerateNewId();
        var switched = ArrangeAccount(ServiceAccountImpersonationSwitchOutcome.SwitchedPendingInvalidation, pipeline);
        _repository.GetServiceAccountsAsync(TenantId).Returns([switched]);
        _repository.GetDataFlowByPipelineAsync(TenantId, pipeline).ThrowsAsync(new InvalidOperationException("db"));

        var report = await CreateSut(allowSecretClearing: true)
            .SwitchTenantAsync(TenantId, apply: true, redeployDataFlows: true);

        using var _ = Assert.Multiple();
        await _provisioning.DidNotReceiveWithAnyArgs().InvalidateSwitchedSecretAsync(default!, default!, default!);
        await Assert.That(For(report, switched).Outcome)
            .IsEqualTo(ServiceAccountImpersonationSwitchOutcome.SwitchedPendingInvalidation);
    }

    [Test]
    public async Task ReRun_CompletesAPendingInvalidation_ForSecretlessAccountsWithActors()
    {
        // The first run cleared the secret but the redeploy failed: the account now reads as
        // AlreadyImpersonating, with its actors. The re-run redeploys and invalidates.
        var pipeline = OctoObjectId.GenerateNewId();
        var pending = ArrangeAccount(ServiceAccountImpersonationSwitchOutcome.AlreadyImpersonating, Actors, pipeline);
        var withoutActors = ArrangeAccount(ServiceAccountImpersonationSwitchOutcome.AlreadyImpersonating,
            OctoObjectId.GenerateNewId());
        _repository.GetServiceAccountsAsync(TenantId).Returns([pending, withoutActors]);
        var flow = ArrangeDataFlow(pipeline);

        var report = await CreateSut(allowSecretClearing: true)
            .SwitchTenantAsync(TenantId, apply: true, redeployDataFlows: true);

        using var _ = Assert.Multiple();
        await _adapterService.Received(1).DeployDataFlowAsync(TenantId, flow.RtId);
        await _provisioning.Received(1).InvalidateSwitchedSecretAsync(TenantId, pending,
            Arg.Any<IReadOnlyList<string>>());
        await _provisioning.DidNotReceive().InvalidateSwitchedSecretAsync(TenantId, withoutActors,
            Arg.Any<IReadOnlyList<string>>());
        await Assert.That(For(report, pending).Outcome)
            .IsEqualTo(ServiceAccountImpersonationSwitchOutcome.AlreadyImpersonating);
        await Assert.That(For(report, pending).Message).Contains("invalidated");
    }

    [Test]
    public async Task DryRun_NeverInvalidates()
    {
        var pipeline = OctoObjectId.GenerateNewId();
        var pending = ArrangeAccount(ServiceAccountImpersonationSwitchOutcome.AlreadyImpersonating, Actors, pipeline);
        _repository.GetServiceAccountsAsync(TenantId).Returns([pending]);
        ArrangeDataFlow(pipeline);

        await CreateSut(allowSecretClearing: true).SwitchTenantAsync(TenantId, apply: false, redeployDataFlows: true);

        using var _ = Assert.Multiple();
        await _adapterService.DidNotReceiveWithAnyArgs().DeployDataFlowAsync(default!, default);
        await _provisioning.DidNotReceiveWithAnyArgs().InvalidateSwitchedSecretAsync(default!, default!, default!);
    }

    // ---------------------------------------------------------------- endpoint

    private static ServiceAccountController CreateController()
    {
        var controller = new ServiceAccountController(NullLogger<ServiceAccountController>.Instance,
            Substitute.For<ICommunicationRepository>(), Substitute.For<IPipelineServiceAccountProvisioningService>(),
            Substitute.For<ICommunicationEventService>());
        var httpContext = new DefaultHttpContext();
        httpContext.Request.RouteValues["tenantId"] = TenantId;
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity([], "TestAuth", "name", "role"));
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return controller;
    }

    [Test]
    public async Task Endpoint_GuardOff_Returns409()
    {
        var switchService = Substitute.For<IServiceAccountImpersonationSwitchService>();
        switchService.SwitchTenantAsync(TenantId, true, true)
            .ThrowsAsync(new ServiceAccountImpersonationSwitchDisabledException());

        var result = await CreateController().SwitchToImpersonation(switchService, apply: true);

        await Assert.That(result).IsTypeOf<ConflictObjectResult>();
    }

    [Test]
    public async Task Endpoint_DefaultsToDryRun_AndMapsTheReport()
    {
        var switchService = Substitute.For<IServiceAccountImpersonationSwitchService>();
        var accountRtId = OctoObjectId.GenerateNewId();
        var pipelineRtId = OctoObjectId.GenerateNewId();
        switchService.SwitchTenantAsync(TenantId, false, true).Returns(new ServiceAccountImpersonationSwitchReport(
            TenantId, false,
            [
                new ServiceAccountImpersonationSwitchResult(accountRtId, "sa", "client",
                    ServiceAccountImpersonationSwitchOutcome.WouldSwitch, "Eligible", [pipelineRtId])
            ], [], new Dictionary<OctoObjectId, string>()));

        var result = await CreateController().SwitchToImpersonation(switchService);

        var dto = (ServiceAccountImpersonationSwitchReportDto)((OkObjectResult)result).Value!;
        using var _ = Assert.Multiple();
        await Assert.That(dto.Applied).IsFalse();
        await Assert.That(dto.Accounts.Single().Outcome).IsEqualTo("WouldSwitch");
        await Assert.That(dto.Accounts.Single().ConfigurationRtId).IsEqualTo(accountRtId.ToString());
        await Assert.That(dto.Accounts.Single().PipelineRtIds.Single()).IsEqualTo(pipelineRtId.ToString());
    }
}
