using Meshmakers.Octo.Backend.CommunicationControllerServices.Consumers;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Nodes.Extracts;

/// <summary>
/// Configuration for node get rt entities by type
/// </summary>
public class GetRtEntitiesByTypeNodeConfiguration : NodeConfiguration
{
    /// <summary>
    /// Amount of items to skip
    /// </summary>
    public int? Skip { get; set; }
    
    /// <summary>
    /// Amount of items to take
    /// </summary>
    public int? Take { get; set; }
    
    /// <summary>
    /// A list of field filters
    /// </summary>
    public ICollection<FieldFilter>? FieldFilters { get; set; }
}

/// <summary>
/// Gets rt entities by type
/// </summary>
[Node("GetRtEntitiesByType", 1, typeof(GetRtEntitiesByTypeNodeConfiguration))]
public class GetRtEntitiesByTypeNode(NodeDelegate next) : IPipelineNode
{
    /// <inheritdoc />
    public async Task ProcessObjectAsync(IDataContext dataContext)
    {
        var etlContext = dataContext.PipelineServiceProvider.GetRequiredService<IRetrieverEtlContext>();

        
        
        
        await next(dataContext);
    }
}