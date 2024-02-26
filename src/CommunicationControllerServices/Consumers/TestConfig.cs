using Meshmakers.Octo.Backend.CommunicationControllerServices.Nodes.Extracts;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Consumers;

internal static class TestConfig
{
     public static PipelineConfigurationRoot Test1 => new()
        {
            Transformations = new List<NodeConfiguration>
            {
                new RetrieveFromMessageNodeConfiguration
                {
                    Description = "Retrieve from distributed event hub message"
                },
                new GetRtEntitiesByIdNodeConfiguration
                {
                    Description = "Retrieve RtEntity if exists",
                    CkTypeId = "IndustryEnergy/EnergyMeter",
                    TargetPropertyName = "EnergyMeterResult",
                    RtIds = new List<OctoObjectId>
                    {
                        new("65dc6d24cc529cdc46c84fcc")
                    }
                },
                new CreateUpdateInfoNodeConfiguration
                {
                    Description = "update",
                    CkTypeId = "IndustryEnergy/EnergyMeter",
                    RtId = new OctoObjectId("65dc6d24cc529cdc46c84fcc"),
                    TargetPropertyName = "_UpdateItems",
                    AttributeUpdates = new List<AttributeUpdateConfiguration>
                    {
                        new() {
                            AttributeName = "Voltage",
                            AttributeValueType = AttributeValueTypesDto.Double,
                            ValuePath = "$.Sinus5"
                        }
                    }
                },
                new ApplyChangesNodeConfiguration
                {
                    TargetPropertyName = "_UpdateItems"
                }
            }
        };
}