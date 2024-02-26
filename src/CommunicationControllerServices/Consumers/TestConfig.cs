using Meshmakers.Octo.Backend.CommunicationControllerServices.Nodes.Extracts;
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
                new GetRtEntitiesByTypeNodeConfiguration
                {
                    Description = "Retrieve",
   
                },
            }
        };
}