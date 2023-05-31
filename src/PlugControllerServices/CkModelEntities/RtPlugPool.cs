using Meshmakers.Octo.SystematizedData.Persistence;
using Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;
using MongoDB.Bson.Serialization.Attributes;
using Newtonsoft.Json;

namespace Meshmakers.Octo.Backend.PlugControllerServices.CkModelEntities;

[CkId(Statics.CkIdPlugPool)]
public class RtPlugPool : RtEntity
{
    [JsonIgnore]
    [BsonIgnore]
    public DateTime? LastSeen
    {
        get => GetAttributeValueOrDefault<DateTime>(nameof(LastSeen));
        set => SetAttributeValue(nameof(LastSeen), AttributeValueTypes.DateTime, value);
    }
    
    [JsonIgnore]
    [BsonIgnore]
    public PlugPoolStates? State
    {
        get => GetAttributeValueOrDefault<PlugPoolStates>(nameof(State));
        set => SetAttributeValue(nameof(State), AttributeValueTypes.Int, value);
    }
    
    [JsonIgnore]
    [BsonIgnore]
    public string? Name
    {
        get => GetAttributeStringValueOrDefault(nameof(Name));
        set => SetAttributeValue(nameof(Name), AttributeValueTypes.String, value);
    }
}