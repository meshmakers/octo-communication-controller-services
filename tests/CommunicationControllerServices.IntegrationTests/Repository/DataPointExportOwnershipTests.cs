using FluentAssertions;
using Meshmakers.Octo.Backend.CommunicationControllerServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.TransportContainer.DTOs;
using Meshmakers.Octo.Runtime.Engine.TransportContainer;
using NSubstitute;
using Xunit;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.IntegrationTests.Repository;

/// <summary>
///     AB#7446 — the export half of the ownership model for the <c>System.Communication/DataPoint</c> record.
///     <c>Loxone/Control.States</c> (and <c>SubControl.States</c>) is a seed-owned RecordArray of <c>DataPoint</c>;
///     the export converter evaluates the record MEMBERS one by one, so a member that is runtime state is lost from
///     the exported record. <c>ExternalId</c> is the identity of a data point (and a required member), so it must be
///     exported; <c>CurrentValue</c> / <c>LastUpdate</c> are live values and must not be.
/// </summary>
/// <remarks>
///     Layer: the real compiled System.Communication model in the real CK cache of the test tenant, driven through
///     the real <see cref="RtEntityToTcDtoConverter" />. Only the entity type holding the RecordArray is
///     hand-built, because <c>DataPoint</c> is used by no type of System.Communication itself (the Loxone adapter
///     model owns <c>Control.States</c>). The export/import round trip lives in octo-bot-services.
/// </remarks>
[Collection("CommunicationController")]
public class DataPointExportOwnershipTests(CommunicationControllerFixture fixture)
{
    private const string CommunicationModel = "System.Communication-4.11.0";

    [Fact]
    public void DataPoint_Name_And_ExternalId_AreSeedOwned_LiveValuesAreRuntimeState()
    {
        var record = fixture.GetService<ICkCacheService>().GetRtCkRecord(fixture.TestTenantId,
            new RtCkId<CkRecordId>("System.Communication/DataPoint"));

        OwnershipOf(record, "Name").Should().Be(AttributeOwnershipDto.SeedOwned);
        OwnershipOf(record, "ExternalId").Should().Be(AttributeOwnershipDto.SeedOwned);
        OwnershipOf(record, "CurrentValue").Should().Be(AttributeOwnershipDto.RuntimeState);
        OwnershipOf(record, "LastUpdate").Should().Be(AttributeOwnershipDto.RuntimeState);
    }

    [Fact]
    public void MappingTarget_SourceIdentifier_StaysRuntimeState()
    {
        var record = fixture.GetService<ICkCacheService>().GetRtCkRecord(fixture.TestTenantId,
            new RtCkId<CkRecordId>("System.Communication/MappingTarget"));

        OwnershipOf(record, "SourceIdentifier").Should().Be(AttributeOwnershipDto.RuntimeState);
    }

    [Fact]
    public void Export_OfASeedOwnedDataPointArray_KeepsNameAndExternalId_DropsTheLiveValues()
    {
        var converter = new RtEntityToTcDtoConverter(CacheWithStatesType());

        var entity = new RtEntity
        {
            RtId = OctoObjectId.GenerateNewId(),
            CkTypeId = new RtCkId<CkTypeId>("Test.Holder/Control")
        };
        entity.SetAttributeRawValue("States", new List<object> { DataPoint("Value", "uuid-1", "21.5") });

        var dto = converter.Convert(fixture.TestTenantId, entity);

        var states = dto.Attributes.Single(a => a.Id.ElementId.Name == "States").Value
            .Should().BeAssignableTo<IEnumerable<object>>().Subject.Cast<RtRecordTcDto>().Single();
        var members = states.Attributes.ToDictionary(a => a.Id.ElementId.Name, a => a.Value);
        members.Should().ContainKey("Name").WhoseValue.Should().Be("Value");
        members.Should().ContainKey("ExternalId").WhoseValue.Should().Be("uuid-1");
        members.Should().NotContainKey("CurrentValue");
        members.Should().NotContainKey("LastUpdate");
    }

    [Fact]
    public async Task Export_OfALoxoneConfiguration_StillDropsMappingTargetsAsAWhole()
    {
        // No side effect on MappingTargets (AB#5187): the RecordArray is runtime state and is skipped as one unit,
        // whatever the ownership of its members is.
        var converter = new RtEntityToTcDtoConverter(fixture.GetService<ICkCacheService>());
        await Task.CompletedTask;

        var target = new RtRecord { CkRecordId = new RtCkId<CkRecordId>("System.Communication/MappingTarget") };
        target.SetAttributeRawValue("SourceIdentifier", "control-uuid");
        target.SetAttributeRawValue("ExternalId", "state-uuid");
        var entity = new RtEntity
        {
            RtId = OctoObjectId.GenerateNewId(),
            CkTypeId = new RtCkId<CkTypeId>("System.Communication/LoxoneConfiguration")
        };
        entity.SetAttributeRawValue("PollIntervalSeconds", 30);
        entity.SetAttributeRawValue("MappingTargets", new List<object> { target });

        var dto = converter.Convert(fixture.TestTenantId, entity);

        dto.Attributes.Select(a => a.Id.ElementId.Name).Should().Contain("PollIntervalSeconds").And.NotContain("MappingTargets");
    }

    private static RtRecord DataPoint(string name, string externalId, string currentValue)
    {
        var record = new RtRecord { CkRecordId = new RtCkId<CkRecordId>("System.Communication/DataPoint") };
        record.SetAttributeRawValue("Name", name);
        record.SetAttributeRawValue("ExternalId", externalId);
        record.SetAttributeRawValue("CurrentValue", currentValue);
        record.SetAttributeRawValue("LastUpdate", new DateTime(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc));
        return record;
    }

    private static AttributeOwnershipDto OwnershipOf(CkRecordGraph record, string name) =>
        record.AllAttributesByName.Values
            .Single(a => a.AttributeName.Equals(name, StringComparison.OrdinalIgnoreCase)).Ownership;

    /// <summary>
    ///     The real cache for everything (the DataPoint record graph), except for the hand-built entity type
    ///     <c>Test.Holder/Control</c> with a seed-owned <c>States</c> RecordArray of
    ///     <c>System.Communication/DataPoint</c> — the shape of <c>Loxone/Control</c>.
    /// </summary>
    private ICkCacheService CacheWithStatesType()
    {
        var real = fixture.GetService<ICkCacheService>();
        var cache = Substitute.For<ICkCacheService>();
        cache.GetRtCkRecord(Arg.Any<string>(), Arg.Any<RtCkId<CkRecordId>>())
            .Returns(call => real.GetRtCkRecord(call.ArgAt<string>(0), call.ArgAt<RtCkId<CkRecordId>>(1)));

        var attributeId = new CkId<CkAttributeId>("Test.Holder-1.0.0/States");
        var definition = new CkAttributeGraph(attributeId, new CkAttributeDto
        {
            AttributeId = "States",
            ValueType = AttributeValueTypesDto.RecordArray,
            ValueCkRecordId = new CkId<CkRecordId>($"{CommunicationModel}/DataPoint"),
            Ownership = AttributeOwnershipDto.SeedOwned
        });
        var states = new CkTypeAttributeGraph(attributeId,
            new CkTypeAttributeDto { CkAttributeId = attributeId, AttributeName = "States" }, definition);
        var type = new CkTypeGraph(
            new CkId<CkTypeId>("Test.Holder-1.0.0/Control"),
            isAbstract: false,
            isFinal: false,
            isCollectionRoot: false,
            baseTypes: [],
            derivedFromCkTypeId: null,
            definingCollectionRootCkTypeId: null,
            derivedTypes: [],
            definedAttributes: [],
            allAttributes: new Dictionary<CkId<CkAttributeId>, CkTypeAttributeGraph> { [attributeId] = states },
            indexes: [],
            associations: new CkGraphDirectedAssociations([]),
            description: "test",
            enableChangeStreamPreAndPostImages: false);
        cache.GetRtCkType(Arg.Any<string>(), Arg.Any<RtCkId<CkTypeId>>()).Returns(type);
        return cache;
    }
}
