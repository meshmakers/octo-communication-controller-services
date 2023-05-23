using Meshmakers.Octo.Communication.Plugs.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Backend.PlugControllerServices.Models;

public class UpdatedPlugConfigurationEventArgs : EventArgs
{
    public PlugConfigurationDto Configuration { get; init; }

    public UpdatedPlugConfigurationEventArgs(PlugConfigurationDto plugConfigurationDto)
    {
        Configuration = plugConfigurationDto;
    }
}