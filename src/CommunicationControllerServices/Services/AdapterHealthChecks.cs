using Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Adapters;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v1;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

/// <summary>
/// Health checks for communication adapters
/// </summary>
internal class AdapterHealthChecks(IAdapterCache adapterCache, ISystemContext systemContext, ICommunicationRepository communicationRepository) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = new CancellationToken())
    {

        using var session = await systemContext.GetAdminSessionAsync();
        session.StartTransaction();

        var tenants = await systemContext.GetChildTenantsAsync(session);
        
        var unhealthyTenants = new Dictionary<string, List<UnhealthyAdapterInfo>>();

        foreach (var tenant in tenants.Items)
        {
            // we are only interested in tenants that have communication enabled
            if (adapterCache.TryGetTenant(tenant.TenantId, out var adapterTenant))
            {
                var tenantId = adapterTenant.TenantId;
                var adapters = await communicationRepository.GetAdaptersAsync(tenantId);
                foreach (var adapter in adapters)
                {
                    if (IsUnhealthy(adapter))
                    {
                        AddUnhealthyAdapter(unhealthyTenants, tenantId, adapter);
                    }
                }
            }
        }
        
        var dataAsObject = unhealthyTenants
            .ToDictionary(kvp => kvp.Key, object (kvp) => kvp.Value);
        
        if(unhealthyTenants.Count > 0)
        {
            return HealthCheckResult.Unhealthy($"Unhealthy adapters found", null, dataAsObject);
        }
        
        return HealthCheckResult.Healthy();
    }

    private bool IsUnhealthy(RtAdapter a)
    {
        return a.CommunicationState != RtCommunicationStateEnum.Online ||
            a.ConfigurationState != RtConfigurationStateEnum.Configured;
    }

    private void AddUnhealthyAdapter(
        Dictionary<string, List<UnhealthyAdapterInfo>> unhealthyTenants,
        string tenantId,
        RtAdapter adapter)
    {
        if (!unhealthyTenants.TryGetValue(tenantId, out var existingAdapters))
        {
            unhealthyTenants[tenantId] = [ToUnhealthyAdapterInfo(adapter)];
        }
        else 
        {
            existingAdapters.Add(ToUnhealthyAdapterInfo(adapter));
        }
    }

    private static UnhealthyAdapterInfo ToUnhealthyAdapterInfo(RtAdapter adapter)
    {
        return new UnhealthyAdapterInfo
        {
            Id = adapter.RtId.ToString(),
            Name = adapter.Name ?? string.Empty,
            CommunicationState = adapter.CommunicationState.ToString(),
            ConfigurationState = adapter.ConfigurationState.ToString(),
            DeploymentState = adapter.DeploymentState.ToString(),
        };
    }
    
    private class UnhealthyAdapterInfo
    {
        public required string Id { get; set; }
        public string? Name { get; set; }
        public required string CommunicationState { get; set; } 
        public required string ConfigurationState { get; set; } 
        public required string DeploymentState { get; set; }    
    }
}