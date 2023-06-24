namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Models;

/// <summary>
/// Contains information for which tenants Communication Controller is enabled. 
/// </summary>
public class CommunicationControllerStatusDto
{
    /// <summary>
    /// Represents the tenant id.
    /// </summary>
    public string TenantId{ get; set; } = null!;
    
    /// <summary>
    /// Represents if the Communication Controller is enabled for the tenant.
    /// </summary>
    public bool IsEnabled { get; set; } = false!;
}