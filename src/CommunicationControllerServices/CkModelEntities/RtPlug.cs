using Meshmakers.Octo.SystematizedData.Persistence;
using Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;
using MongoDB.Bson.Serialization.Attributes;
using Newtonsoft.Json;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.CkModelEntities;

[CkId(Statics.CkIdPlug)]
public class RtPlug : RtEntity
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
    public PlugStates? State
    {
        get => GetAttributeValueOrDefault<PlugStates>(nameof(State));
        set => SetAttributeValue(nameof(State), AttributeValueTypes.Int, value);
    }
    
    [JsonIgnore]
    [BsonIgnore]
    public string? Type
    {
        get => GetAttributeStringValueOrDefault(nameof(Type));
        set => SetAttributeValue(nameof(Type), AttributeValueTypes.String, value);
    }
    
        
    [JsonIgnore]
    [BsonIgnore]
    public string? Configuration
    {
        get => GetAttributeStringValueOrDefault(nameof(Configuration));
        set => SetAttributeValue(nameof(Configuration), AttributeValueTypes.String, value);
    }
    
    [JsonIgnore]
    [BsonIgnore]
    public string? ImageName
    {
        get => GetAttributeStringValueOrDefault(nameof(ImageName));
        set => SetAttributeValue(nameof(ImageName), AttributeValueTypes.String, value);
    }
    
    [JsonIgnore]
    [BsonIgnore]
    public string? ImageVersion
    {
        get => GetAttributeStringValueOrDefault(nameof(ImageVersion));
        set => SetAttributeValue(nameof(ImageVersion), AttributeValueTypes.String, value);
    }
}