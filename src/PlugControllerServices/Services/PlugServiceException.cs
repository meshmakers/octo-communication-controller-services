namespace Meshmakers.Octo.Backend.PlugControllerServices.Services;

public class PlugServiceException : Exception
{
    public PlugServiceException()
    {
    }

    public PlugServiceException(string message) : base(message)
    {
    }

    public PlugServiceException(string message, Exception inner) : base(message, inner)
    {
    }
}

