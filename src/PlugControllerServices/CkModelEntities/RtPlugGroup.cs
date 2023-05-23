using Meshmakers.Octo.SystematizedData.Persistence;
using Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;
using MongoDB.Bson.Serialization.Attributes;
using Newtonsoft.Json;

namespace Meshmakers.Octo.Backend.PlugControllerServices.CkModelEntities;

[CkId(Statics.CkIdPlugGroup)]
public class RtPlugGroup : RtEntity
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
    public string? Description
    {
        get => GetAttributeStringValueOrDefault(nameof(Description));
        set => SetAttributeValue(nameof(Description), AttributeValueTypes.String, value);
    }
}