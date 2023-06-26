namespace Meshmakers.Octo.Backend.CommunicationControllerServices.CkModelEntities;

/// <summary>
/// Represents the state of a communication adapter.
/// </summary>
public enum AdapterStates
{
    /// <summary>
    /// Adapter is created in database but communication controller has not yet seen it.
    /// </summary>
    Created = 0,
    
    /// <summary>
    /// Adapter is created in database and communication controller has seen it.
    /// </summary>
    Pending = 1,
    
    /// <summary>
    /// Adapter is deployed but not yet online.
    /// </summary>
    Deployed = 2,
    
    /// <summary>
    /// Adapter was online but is now offline.
    /// </summary>
    Offline = 3,
    
    /// <summary>
    /// Adapter is online.
    /// </summary>
    Online = 4,
    
    /// <summary>
    /// During deployment an error occurred.
    /// </summary>
    DeploymentError = 5,
    
    /// <summary>
    /// The configuration of the adapter is invalid, so it cannot be deployed.
    /// </summary>
    ConfigurationError = 6
}