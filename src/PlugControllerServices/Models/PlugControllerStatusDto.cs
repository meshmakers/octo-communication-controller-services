namespace Meshmakers.Octo.Backend.PlugControllerServices.Models;

/// <summary>
/// Contains information for which tenants Plug Controller is enabled. 
/// </summary>
public class PlugControllerStatusDto
{
    /// <summary>
    /// Represents the tenant id.
    /// </summary>
    public string TenantId{ get; set; } = null!;
    
    /// <summary>
    /// Represents if the Plug Controller is enabled for the tenant.
    /// </summary>
    public bool IsEnabled { get; set; } = false!;
}