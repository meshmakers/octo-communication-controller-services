using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v2;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;

internal static class RtEntityCreator
{
    public static RtDataPipeline CreateDataPipeline(string? id = null)
    {
        id ??= OctoObjectId.GenerateNewId().ToString();
        return new RtDataPipeline
        {
            RtId = new OctoObjectId(id),
            CkTypeId = SystemCommunicationCkIds.RtCkDataPipelineTypeId
        };
    }

    public static RtPipeline CreatePipeline(string? pipelineDefinition = null, string? id = null)
    {
        id = id ?? OctoObjectId.GenerateNewId().ToString();
        return new RtPipeline
        {
            RtId = new OctoObjectId(id),
            CkTypeId = SystemCommunicationCkIds.RtCkMeshPipelineTypeId,
            PipelineDefinition = pipelineDefinition ?? "pipelineDefinition",
            DeploymentState = RtDeploymentStateEnum.Deployed
        };
    }

    public static RtAdapter CreateAdapter(string? id = null)
    {
        id = id ?? OctoObjectId.GenerateNewId().ToString();
        return new RtAdapter
        {
            RtId = new OctoObjectId(id),
            CkTypeId = SystemCommunicationCkIds.RtCkMeshAdapterTypeId
        };
    }
}
