using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Backend.CommunicationControllerServices.TenantApi.v1.Controllers;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Services.Infrastructure.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Controllers;

/// <summary>
/// Pins the shape of GET /workload-variables. The endpoint is the single
/// suggestion source for the Studio's workload editor across all three
/// template families ({{context.tenantId}}, {{domain.NAME}},
/// {{service.NAME}}) — other endpoints should not duplicate this list.
/// </summary>
internal class CommunicationControllerWorkloadVariablesTests
{
    private static CommunicationController CreateSut(IWorkloadTemplateResolver resolver)
    {
        var configurationService = Substitute.For<IConfigurationService>();
        var expressionValidationService = Substitute.For<IExpressionValidationService>();
        var encryptionService = Substitute.For<IWorkloadEncryptionService>();
        return new CommunicationController(NullLogger<CommunicationController>.Instance,
            configurationService, expressionValidationService, encryptionService, resolver);
    }

    [Test]
    public async Task GetWorkloadVariables_AlwaysIncludesContextTenantIdWithNullSampleValue()
    {
        // {{context.tenantId}} is per-deploy — the cluster config doesn't carry
        // a sample value, so SampleValue must be null. Pins that the endpoint
        // is the discovery point even on a fresh install without any configured
        // domains or service URLs.
        var resolver = Substitute.For<IWorkloadTemplateResolver>();
        resolver.AvailableDomains.Returns(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        resolver.AvailableServiceUrls.Returns(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        var sut = CreateSut(resolver);

        var result = sut.GetWorkloadVariables();

        var ok = result as OkObjectResult;
        await Assert.That(ok).IsNotNull();
        var dtos = (ok!.Value as IEnumerable<WorkloadVariableDto>)!.ToList();
        await Assert.That(dtos.Count).IsEqualTo(1);
        await Assert.That(dtos[0].Placeholder).IsEqualTo("{{context.tenantId}}");
        await Assert.That(dtos[0].SampleValue).IsNull();
    }

    [Test]
    public async Task GetWorkloadVariables_ProjectsDomainsAndServiceUrlsAlphabetically()
    {
        // Stable ordering inside each family so the UI list doesn't shuffle on
        // every reload. context.tenantId stays at index 0 (added first by the
        // endpoint), then domains, then service URLs.
        var resolver = Substitute.For<IWorkloadTemplateResolver>();
        resolver.AvailableDomains.Returns(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["internal"] = "octo.internal",
            ["default"] = "staging.octo-mesh.com",
        });
        resolver.AvailableServiceUrls.Returns(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["bot"] = "https://bot.staging.octo-mesh.com",
            ["authority"] = "https://identity.staging.octo-mesh.com",
        });
        var sut = CreateSut(resolver);

        var dtos = ((sut.GetWorkloadVariables() as OkObjectResult)!
            .Value as IEnumerable<WorkloadVariableDto>)!.ToList();

        await Assert.That(dtos.Count).IsEqualTo(5);
        await Assert.That(dtos[0].Placeholder).IsEqualTo("{{context.tenantId}}");
        // Domains alphabetical
        await Assert.That(dtos[1].Placeholder).IsEqualTo("{{domain.default}}");
        await Assert.That(dtos[1].SampleValue).IsEqualTo("staging.octo-mesh.com");
        await Assert.That(dtos[2].Placeholder).IsEqualTo("{{domain.internal}}");
        await Assert.That(dtos[2].SampleValue).IsEqualTo("octo.internal");
        // Service URLs alphabetical
        await Assert.That(dtos[3].Placeholder).IsEqualTo("{{service.authority}}");
        await Assert.That(dtos[3].SampleValue).IsEqualTo("https://identity.staging.octo-mesh.com");
        await Assert.That(dtos[4].Placeholder).IsEqualTo("{{service.bot}}");
        await Assert.That(dtos[4].SampleValue).IsEqualTo("https://bot.staging.octo-mesh.com");
    }
}
