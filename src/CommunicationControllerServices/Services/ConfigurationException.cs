namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

/// <summary>
/// Exception for configuration errors
/// </summary>
public class ConfigurationException : Exception
{
    private ConfigurationException()
    {
    }

    private ConfigurationException(string message) : base(message)
    {
    }

    private ConfigurationException(string message, Exception inner) : base(message, inner)
    {
    }

    internal static Exception TenantAlreadyEnabled(string tenantId)
    {
        return new ConfigurationException($"Tenant '{tenantId}' is already enabled.");
    }

    internal static Exception TenantAlreadyDisabled(string tenantId)
    {
        return new ConfigurationException($"Tenant '{tenantId}' is already disabled.");
    }
}
