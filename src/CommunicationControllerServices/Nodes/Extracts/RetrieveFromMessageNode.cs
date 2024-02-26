using Meshmakers.Octo.Backend.CommunicationControllerServices.Consumers;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Newtonsoft.Json.Linq;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Nodes.Extracts;

internal class RetrieveFromMessageNodeConfiguration : NodeConfiguration;

[Node("RetrieveFromMessage", 1, typeof(RetrieveFromMessageNodeConfiguration))]
internal class RetrieveFromMessageNode(NodeDelegate next) : IPipelineNode
{
    public async Task ProcessObjectAsync(IDataContext dataContext)
    {
        var etlContext = dataContext.PipelineServiceProvider.GetRequiredService<IRetrieverEtlContext>();

        dataContext.Current = JObject.FromObject(etlContext.Message ?? new JObject());

        await next(dataContext);
    }
}