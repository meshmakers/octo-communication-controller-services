using Meshmakers.Octo.Backend.CommunicationControllerServices.Consumers;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Nodes.Extracts;

/// <summary>
/// Configuration for node get rt entities by id
/// </summary>
public class GetRtEntitiesByIdNodeConfiguration : NodeConfiguration
{
    /// <summary>
    /// Gets or sets the target property name
    /// </summary>
    public string? TargetPropertyName { get; set; }
    
    /// <summary>
    /// CkTypeId of query
    /// </summary>
    public CkId<CkTypeId>? CkTypeId { get; set; }
    
    /// <summary>
    /// Amount of items to skip
    /// </summary>
    public int? Skip { get; set; }
    
    /// <summary>
    /// Amount of items to take
    /// </summary>
    public int? Take { get; set; }
    
    /// <summary>
    /// Gets or sets the rt ids
    /// </summary>
    public ICollection<OctoObjectId>? RtIds { get; set; }
    
    /// <summary>
    /// A list of field filters
    /// </summary>
    public ICollection<FieldFilter>? FieldFilters { get; set; }
}

/// <summary>
/// Gets rt entities by type
/// </summary>
[Node("GetRtEntitiesById", 1, typeof(GetRtEntitiesByIdNodeConfiguration))]
public class GetRtEntitiesByIdNode(NodeDelegate next) : IPipelineNode
{
    /// <inheritdoc />
    public async Task ProcessObjectAsync(IDataContext dataContext)
    {
        var etlContext = dataContext.PipelineServiceProvider.GetRequiredService<IRetrieverEtlContext>();

        var c = dataContext.GetNodeConfiguration<GetRtEntitiesByIdNodeConfiguration>();

        if (c.CkTypeId == null)
        {
            dataContext.Logger.LogError("CkTypeId is not set");
            return;
        }

        if (c.RtIds == null)
        {
            dataContext.Logger.LogError("RtIds is not set");
            return;
        }

        var dataQueryOperation = DataQueryOperation.Create();
        if (c.FieldFilters != null)
        {
            foreach (var fieldFilter in c.FieldFilters)
            {
                dataQueryOperation.AddFieldFilter(fieldFilter.AttributeName, fieldFilter.Operator, fieldFilter.ComparisonValue);
            }
        }

        var r = await etlContext.TenantRepository.GetRtEntitiesByIdAsync(etlContext.Session, c.CkTypeId, c.RtIds.ToList(), dataQueryOperation, c.Skip, c.Take);

        dataContext.SetCurrentValueByName(c.TargetPropertyName, r);
        
        await next(dataContext);
    }
}