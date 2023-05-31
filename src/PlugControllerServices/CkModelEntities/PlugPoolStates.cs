namespace Meshmakers.Octo.Backend.PlugControllerServices.CkModelEntities;

public enum PlugPoolStates
{
    Created = 0,
    Pending = 1,
    Deployed = 2,
    Offline = 3,
    Online = 4,
    DeploymentError = 5,
    ConfigurationError = 6
}