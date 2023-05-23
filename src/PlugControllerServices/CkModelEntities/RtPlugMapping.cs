using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.SystematizedData.Persistence;
using Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;
using MongoDB.Bson.Serialization.Attributes;
using Newtonsoft.Json;

namespace Meshmakers.Octo.Backend.PlugControllerServices.CkModelEntities;

[CkId(Statics.CkIdPlugMapping)]
public class RtPlugMapping: RtEntity
{
    [JsonIgnore]
    [BsonIgnore]
    public string? Designation
    {
        get => GetAttributeStringValueOrDefault(nameof(Designation));
        set => SetAttributeValue(nameof(Designation), AttributeValueTypes.String, value);
    }
    
    [JsonIgnore]
    [BsonIgnore]
    public string? ReferenceId
    {
        get => GetAttributeStringValueOrDefault(nameof(ReferenceId));
        set => SetAttributeValue(nameof(ReferenceId), AttributeValueTypes.String, value);
    }
    
    [JsonIgnore]
    [BsonIgnore]
    public string? ReferenceCkId
    {
        get => GetAttributeStringValueOrDefault(nameof(ReferenceCkId));
        set => SetAttributeValue(nameof(ReferenceCkId), AttributeValueTypes.String, value);
    }
    
    [JsonIgnore]
    [BsonIgnore]
    public string? ReferenceAttributeId
    {
        get => GetAttributeStringValueOrDefault(nameof(ReferenceAttributeId));
        set => SetAttributeValue(nameof(ReferenceAttributeId), AttributeValueTypes.String, value);
    }
    
    [JsonIgnore]
    [BsonIgnore]
    public string? Configuration
    {
        get => GetAttributeStringValueOrDefault(nameof(Configuration));
        set => SetAttributeValue(nameof(Configuration), AttributeValueTypes.String, value);
    }
}