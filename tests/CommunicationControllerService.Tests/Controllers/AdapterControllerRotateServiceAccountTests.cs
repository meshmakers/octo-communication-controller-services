using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Models;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Backend.CommunicationControllerServices.TenantApi.v1.Controllers;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v3;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Controllers;

/// <summary>
/// AB#5032 — <c>POST {tenantId}/v1/adapter/{adapterRtId}/serviceAccount/rotateSecret</c>.
/// </summary>
internal class AdapterControllerRotateServiceAccountTests
{
    private const string TenantId = "acme";

    private static (AdapterController sut, ICommunicationRepository repo,
        IPipelineServiceAccountProvisioningService provisioning, ICommunicationEventService events) CreateSut()
    {
        var repo = Substitute.For<ICommunicationRepository>();
        var adapters = Substitute.For<IAdapterService>();
        var provisioning = Substitute.For<IPipelineServiceAccountProvisioningService>();
        var events = Substitute.For<ICommunicationEventService>();
        var sut = new AdapterController(NullLogger<AdapterController>.Instance, repo, adapters);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.RouteValues["tenantId"] = TenantId;
        sut.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return (sut, repo, provisioning, events);
    }

    [Test]
    public async Task Rotate_HappyPath_ReturnsTheRedeployHintAndWritesAnAuditEvent()
    {
        var (sut, repo, provisioning, events) = CreateSut();
        var adapter = RtEntityCreator.CreateAdapter();
        adapter.Name = "mesh-adapter";
        repo.GetAdaptersAsync(TenantId).Returns([adapter]);
        provisioning.RotateAdapterSecretAsync(TenantId, adapter)
            .Returns(new PipelineServiceAccountRotationResult("octo-pipeline-sa-1",
                "pipeline-service-account-1", WasCreated: false));

        var result = await sut.RotateServiceAccountSecret(adapter.RtId.ToString(), provisioning, events);

        var ok = result as OkObjectResult;
        await Assert.That(ok).IsNotNull();
        var dto = ok!.Value as RotateServiceAccountSecretResultDto;

        using var _ = Assert.Multiple();
        await Assert.That(dto).IsNotNull();
        await Assert.That(dto!.ClientId).IsEqualTo("octo-pipeline-sa-1");
        await Assert.That(dto.ConfigurationWellKnownName).IsEqualTo("pipeline-service-account-1");
        await Assert.That(dto.WasCreated).IsFalse();
        // 🔴 The operator must be told: the adapter caches the credentials at pipeline registration,
        // so nothing changes for a running pipeline until it is redeployed.
        await Assert.That(dto.RequiresPipelineRedeploy).IsTrue();
        await Assert.That(dto.Message).Contains("Redeploy");

        await events.Received(1).StoreInformationEventAsync(TenantId,
            Arg.Is<string>(m => m.Contains("octo-pipeline-sa-1") && m.Contains("(source: User)")));
    }

    [Test]
    public async Task Rotate_ResponseCarriesNoSecretShapedProperty()
    {
        // The plaintext lives in exactly two places; an API that returns a third would put it into
        // proxy logs, shell history and CI output.
        var properties = typeof(RotateServiceAccountSecretResultDto).GetProperties()
            .Select(p => p.Name)
            .ToList();

        await Assert.That(properties.Any(n => n.Contains("Secret", StringComparison.OrdinalIgnoreCase)))
            .IsFalse();
    }

    [Test]
    public async Task Rotate_AdapterWithoutAccount_ReportsAProvisioningAndNoRedeploy()
    {
        var (sut, repo, provisioning, events) = CreateSut();
        var adapter = RtEntityCreator.CreateAdapter();
        repo.GetAdaptersAsync(TenantId).Returns([adapter]);
        provisioning.RotateAdapterSecretAsync(TenantId, adapter)
            .Returns(new PipelineServiceAccountRotationResult("octo-pipeline-sa-1",
                "pipeline-service-account-1", WasCreated: true));

        var result = await sut.RotateServiceAccountSecret(adapter.RtId.ToString(), provisioning, events);

        var dto = (result as OkObjectResult)!.Value as RotateServiceAccountSecretResultDto;

        using var _ = Assert.Multiple();
        await Assert.That(dto!.WasCreated).IsTrue();
        await Assert.That(dto.RequiresPipelineRedeploy).IsFalse();
        await Assert.That(dto.Message).Contains("Nothing was invalidated");
    }

    [Test]
    public async Task Rotate_UnknownAdapter_Returns404AndNeverCallsTheProvisioningService()
    {
        var (sut, repo, provisioning, events) = CreateSut();
        repo.GetAdaptersAsync(TenantId).Returns([]);

        var result = await sut.RotateServiceAccountSecret(OctoObjectId.GenerateNewId().ToString(),
            provisioning, events);

        using var _ = Assert.Multiple();
        await Assert.That(result).IsTypeOf<NotFoundObjectResult>();
        await provisioning.DidNotReceiveWithAnyArgs().RotateAdapterSecretAsync(default!, default!);
    }

    [Test]
    public async Task Rotate_MalformedAdapterRtId_Returns400()
    {
        var (sut, _, provisioning, events) = CreateSut();

        var result = await sut.RotateServiceAccountSecret("not-an-object-id", provisioning, events);

        using var _ = Assert.Multiple();
        await Assert.That(result).IsTypeOf<BadRequestObjectResult>();
        await provisioning.DidNotReceiveWithAnyArgs().RotateAdapterSecretAsync(default!, default!);
    }

    [Test]
    public async Task Rotate_Failure_Returns400StatingThatTheOldSecretStillWorks()
    {
        var (sut, repo, provisioning, events) = CreateSut();
        var adapter = RtEntityCreator.CreateAdapter();
        repo.GetAdaptersAsync(TenantId).Returns([adapter]);
        provisioning.RotateAdapterSecretAsync(TenantId, adapter)
            .ThrowsAsync(new InvalidOperationException("identity refused"));

        var result = await sut.RotateServiceAccountSecret(adapter.RtId.ToString(), provisioning, events);

        var badRequest = result as BadRequestObjectResult;
        var error = badRequest!.Value as ErrorResponse;

        using var _ = Assert.Multiple();
        // A caller who is told "failed" but not "the old one still works" would go and reconfigure
        // things that are not broken.
        await Assert.That(error!.ErrorMessage).Contains("previous secret remains in effect");
        await Assert.That(error.ErrorMessage).Contains("identity refused");
        await events.Received(1).StoreErrorEventAsync(TenantId,
            Arg.Is<string>(m => m.Contains("identity refused")));
    }
}
