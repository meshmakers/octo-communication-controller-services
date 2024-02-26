using Meshmakers.Octo.Backend.CommunicationControllerServices.Consumers;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Newtonsoft.Json.Linq;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Nodes.Extracts;

/// <summary>
/// Configuration node object for update a rt entity object
/// </summary>
public class CreateUpdateInfoNodeConfiguration : NodeConfiguration
{
    /// <summary>
    /// Gets or sets the target property name
    /// </summary>
    public string? TargetPropertyName { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to append to the target property name
    /// </summary>
    public bool AppendToTargetPropertyName { get; set; } = true;
    
    /// <summary>
    /// The path to the RtEntityId
    /// </summary>
    public string? RtIdPath { get; set; }

    /// <summary>
    /// The runtime id of the object
    /// </summary>
    public OctoObjectId? RtId { get; set; }

    /// <summary>
    /// CkTypeId of query
    /// </summary>
    public CkId<CkTypeId>? CkTypeId { get; set; }

    /// <summary>
    /// Updates to the RtEntity attributes
    /// </summary>
    public ICollection<AttributeUpdateConfiguration>? AttributeUpdates { get; set; }
}

/// <summary>
/// Update of a specific attribute
/// </summary>
public class AttributeUpdateConfiguration
{
    /// <summary>
    /// Defines the attribute name path
    /// </summary>
    public string? AttributeName { get; set; }

    /// <summary>
    /// Gets or sets the attribute value type
    /// </summary>
    public AttributeValueTypesDto? AttributeValueType { get; set; }

    /// <summary>
    /// Value path
    /// </summary>
    public string? ValuePath { get; set; }
}

/// <summary>
/// Creates an update item for an existing RtEntity
/// </summary>
[Node("CreateUpdateInfo", 1, typeof(CreateUpdateInfoNodeConfiguration))]
public class CreateUpdateInfoNode(NodeDelegate next) : IPipelineNode
{
    /// <inheritdoc />
    public async Task ProcessObjectAsync(IDataContext dataContext)
    {
        var c = dataContext.GetNodeConfiguration<CreateUpdateInfoNodeConfiguration>();

        if (c.CkTypeId == null)
        {
            dataContext.Logger.LogError("CkTypeId is not set");
            return;
        }

        if (c.RtId == null && c.RtIdPath == null)
        {
            dataContext.Logger.LogError("RtId and RtIdPath is not set");
            return;
        }

        if (c.AttributeUpdates == null || c.AttributeUpdates.Count == 0)
        {
            dataContext.Logger.LogError("AttributeUpdates is not set");
            return;
        }

        var rtId = c.RtId ?? dataContext.Current?.SelectToken(c.RtIdPath ?? "$")?.ToObject<OctoObjectId>();

        if (rtId == null)
        {
            dataContext.Logger.LogError("RtId is not set");
            return;
        }

        var list = c.TargetPropertyName == null ?
            new List<IEntityUpdateInfo<RtEntity>>() : 
            dataContext.GetCurrentValueByPath<List<IEntityUpdateInfo<RtEntity>>>(c.TargetPropertyName) ?? new List<IEntityUpdateInfo<RtEntity>>();
        var rtEntity = new RtEntity();
        foreach (var au in c.AttributeUpdates)
        {
            if (string.IsNullOrWhiteSpace(au.AttributeName))
            {
                dataContext.Logger.LogError("Attribute name is not set");
                continue;
            }

            if (au.AttributeValueType == null)
            {
                dataContext.Logger.LogError("Attribute value type is not set");
                continue;
            }

            var jToken = dataContext.Current?.SelectToken(au.ValuePath ?? "$");
            object? value = null;
            if (jToken != null && jToken is JValue jValue)
            {
                switch (au.AttributeValueType.Value)
                {
                    case AttributeValueTypesDto.Double:
                        value = jValue.Value<double>();
                        break;
                    case AttributeValueTypesDto.Int:
                        value = jValue.Value<int>();
                        break;                    
                }
            }

            rtEntity.SetAttributeValue(au.AttributeName, au.AttributeValueType.Value, value);
        }
        list.Add(EntityUpdateInfo<RtEntity>.CreateUpdate(new RtEntityId(c.CkTypeId, rtId.Value), rtEntity));
      
        dataContext.SetCurrentValueByName(c.TargetPropertyName, list);

        await next(dataContext);
    }
}