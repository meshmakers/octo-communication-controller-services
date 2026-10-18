using Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Adapters;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Methods;

/// <summary>Where the pipeline handler finds method definitions.</summary>
internal interface IPipelineMethodCatalog
{
    /// <summary>All methods declared <c>handler: pipeline</c> in the tenants this controller serves.</summary>
    IReadOnlyList<PipelineMethodDefinition> GetPipelineMethods();

    /// <summary>
    ///     The definition of a method in a tenant, whatever its handler kind, or <c>null</c> when the tenant or the
    ///     method is unknown. The handler kind decides whether the controller answers the call.
    /// </summary>
    PipelineMethodDefinition? Find(string tenantId, string qualifiedMethodId);

    /// <summary>True when the controller serves the tenant (the tenant is communication-enabled).</summary>
    bool IsTenantEnabled(string tenantId);
}

/// <summary>Reads the method definitions from the per-tenant CK cache of the controller.</summary>
internal sealed class PipelineMethodCatalog(ICkCacheService ckCache, IAdapterCache adapterCache) : IPipelineMethodCatalog
{
    public IReadOnlyList<PipelineMethodDefinition> GetPipelineMethods()
    {
        var result = new List<PipelineMethodDefinition>();
        foreach (var tenantId in adapterCache.GetEnabledTenantIds())
        {
            if (!ckCache.IsTenantLoaded(tenantId))
            {
                continue;
            }

            foreach (var type in ckCache.GetCkTypes(tenantId))
            {
                // only methods the type declares itself, inherited ones are listed by their declaring type
                foreach (var method in type.AllMethods.Values.Where(m =>
                             m.DeclaringCkTypeId == type.CkTypeId &&
                             m.Handler == CkMethodHandlers.Pipeline))
                {
                    result.Add(new PipelineMethodDefinition(tenantId, method.QualifiedMethodId, method.Definition));
                }
            }
        }

        return result;
    }

    public PipelineMethodDefinition? Find(string tenantId, string qualifiedMethodId)
    {
        var graph = FindGraph(tenantId, qualifiedMethodId);
        return graph is null ? null : new PipelineMethodDefinition(tenantId, qualifiedMethodId, graph.Definition);
    }

    public bool IsTenantEnabled(string tenantId) => adapterCache.TryGetTenant(tenantId, out _);

    private Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph.CkMethodGraph? FindGraph(string tenantId,
        string qualifiedMethodId)
    {
        if (!ckCache.IsTenantLoaded(tenantId) ||
            !CkMethodIds.TryParse(qualifiedMethodId, out var declaringTypeId, out var methodId))
        {
            return null;
        }

        return ckCache.TryGetRtCkType(tenantId, declaringTypeId, out var type) &&
               type.AllMethods.TryGetValue(methodId, out var method)
            ? method
            : null;
    }
}
