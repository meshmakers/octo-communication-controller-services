namespace Meshmakers.Octo.Backend.DeviceManagementServices.Models;

public class UpdatedPlugConfigurationEventArgs : EventArgs
{
    public PlugConfigurationDto Configuration { get; init; }

    public UpdatedPlugConfigurationEventArgs(PlugConfigurationDto plugConfigurationDto)
    {
        Configuration = plugConfigurationDto;
    }
}