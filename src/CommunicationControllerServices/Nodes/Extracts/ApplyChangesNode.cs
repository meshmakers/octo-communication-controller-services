using Meshmakers.Octo.Backend.CommunicationControllerServices.Consumers;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Nodes.Extracts;

/// <summary>
/// Configuration node object for apply changes to the object in mongodb
/// </summary>
public class ApplyChangesNodeConfiguration : NodeConfiguration
{
    /// <summary>
    /// Gets or sets the target property name
    /// </summary>
    public string? TargetPropertyName { get; set; }
}

/// <summary>
/// Applies changes to the object in mongodb
/// </summary>
[Node("ApplyChanges", 1, typeof(ApplyChangesNodeConfiguration))]
public class ApplyChangesNode(NodeDelegate next) : IPipelineNode
{
    /// <inheritdoc />
    public async Task ProcessObjectAsync(IDataContext dataContext)
    {
        var etlContext = dataContext.PipelineServiceProvider.GetRequiredService<IRetrieverEtlContext>();
        var c = dataContext.GetNodeConfiguration<ApplyChangesNodeConfiguration>();

        var list = c.TargetPropertyName == null ?
            new List<IEntityUpdateInfo<RtEntity>>() : 
            dataContext.GetCurrentValueByPath<List<IEntityUpdateInfo<RtEntity>>>(c.TargetPropertyName) ?? new List<IEntityUpdateInfo<RtEntity>>();

        if (list.Any())
        {
            OperationResult operationResult = new();
            await etlContext.TenantRepository.ApplyChangesAsync(etlContext.Session, list, operationResult);
            if (operationResult.HasErrors || operationResult.HasFatalErrors)
            {
                dataContext.Logger.LogError("Error updating RtEntity");
                return;
            }
        }

        await next(dataContext);
    }
}