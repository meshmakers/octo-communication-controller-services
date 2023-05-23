namespace Meshmakers.Octo.Backend.PlugControllerServices.Services;

public class PlugInManagementException : Exception
{
    public PlugInManagementException()
    {
    }

    public PlugInManagementException(string message) : base(message)
    {
    }

    public PlugInManagementException(string message, Exception inner) : base(message, inner)
    {
    }
}

