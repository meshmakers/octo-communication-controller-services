using FluentAssertions;
using Meshmakers.Octo.Backend.CommunicationControllerServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts;
using Xunit;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.IntegrationTests.Repository;

/// <summary>
///     AB#8594: System.Communication 4.16.0 is the first ckLanguage 2 release. The CK cache of a tenant is rebuilt from the
///     model stored in MongoDB, not from the compiled file, so the CK v2 keys must survive the import and the read-back:
///     a lost <c>derivable: Any</c> would make the cache apply the ckLanguage 2 default <c>Model</c> and every adapter or
///     energy model that derives from a System.Communication type would stop resolving; a lost <c>access: Hidden</c> would
///     expose <c>SecretDefault.Value</c> through the generic API again. A second import of the same version (a service
///     restart) must succeed without errors. The controller's own repository write of the Hidden value is covered by
///     <see cref="SecretDefaultRepositoryTests" />.
/// </summary>
[Collection("CommunicationController")]
public class SystemCommunicationCkLanguage2PersistenceTests(CommunicationControllerFixture fixture)
{
    [Fact]
    public async Task ImportedSystemCommunication_KeepsTheCkV2Modifiers_AndAReImportSucceeds()
    {
        var tenantContext = await fixture.GetTestTenantContextAsync();
        var operationResult = new OperationResult();
        await tenantContext.ImportCkModelAsync(new CkModelId("System.Communication-4.16.0"), operationResult);
        operationResult.HasErrors.Should().BeFalse("re-import (service restart): {0}", operationResult);

        var cache = fixture.GetService<ICkCacheService>();
        foreach (var typeId in new[]
                 {
                     "System.Communication/Adapter", "System.Communication/AdapterPool",
                     "System.Communication/DeployableWorkload", "System.Communication/Pipeline"
                 })
        {
            var type = cache.GetRtCkType(fixture.TestTenantId, new RtCkId<CkTypeId>(typeId));
            type.Derivable.Should().Be(CkDerivableDto.Any, typeId);
        }

        var dataFlow = cache.GetRtCkType(fixture.TestTenantId,
            new RtCkId<CkTypeId>("System.Communication/DataFlow"));
        dataFlow.AllImplementedInterfaces.Select(i => $"{i.ModelId.Name}/{i.ElementId.Name}")
            .Should().BeEquivalentTo("System/Named", "System/Describable");

        var pipeline = cache.GetRtCkType(fixture.TestTenantId,
            new RtCkId<CkTypeId>("System.Communication/Pipeline"));
        pipeline.AllImplementedInterfaces.Select(i => $"{i.ModelId.Name}/{i.ElementId.Name}")
            .Should().BeEquivalentTo(["System/Describable"], "Pipeline inherits Describable-1 from DeployableEntity");

        var secretDefault = cache.GetRtCkType(fixture.TestTenantId,
            new RtCkId<CkTypeId>("System.Communication/SecretDefault"));
        secretDefault.AllAttributesByName["Value"].Access.Should().Be(CkAttributeAccessDto.Hidden);
        secretDefault.AllAttributesByName["Name"].Access.Should().Be(CkAttributeAccessDto.ReadWrite);
    }
}
