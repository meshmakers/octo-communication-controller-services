using Meshmakers.Octo.SystematizedData.Persistence;
using Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;
using MongoDB.Bson.Serialization.Attributes;
using Newtonsoft.Json;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.CkModelEntities;

[CkId(Statics.CkIdSocket)]
internal class RtSocket : RtCommunicationAdapter
{
    [JsonIgnore]
    [BsonIgnore]
    public string? Configuration
    {
        get => GetAttributeStringValueOrDefault(nameof(Configuration));
        set => SetAttributeValue(nameof(Configuration), AttributeValueTypes.String, value);
    }
}