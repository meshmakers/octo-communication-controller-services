namespace Meshmakers.Octo.Backend.CommunicationControllerServices.CkModelEntities;

public enum PlugStates
{
    Created = 0,
    Pending = 1,
    Deployed = 2,
    Offline = 3,
    Online = 4,
    DeploymentError = 5,
    ConfigurationError = 6
}