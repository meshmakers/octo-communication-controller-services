using Meshmakers.Octo.Communication.Plugs.Contracts.Configuration;

namespace Meshmakers.Octo.Backend.DeviceManagementServices;

internal static class Statics
{
    public const string TenantId = "tenantId";

    public static string? GetTenantId(this HttpContext httpContext)
    {
        return (string?)httpContext.GetRouteValue(TenantId);
    }


    public static readonly PlugConfiguration PlugTestConfig = new()
    {
        ServerConfigurations = new[]
        {
            new ServerConfiguration
            {
                Server = "192.168.13.31", Groups = new[]
                {
                    new GroupConfiguration
                    {
                        Name = "General",
                        Id = new Guid("{2E902575-E892-402C-BCC1-DB4F433556EC}"),
                        Mappings = new[]
                        {
                            new MappingConfiguration
                            {
                                Name = "Power Battery",
                                Configuration = "{ \"register\": 69, \"registerType\": 3 }"
                            },
                            new MappingConfiguration
                            {
                                Name = "Power PV",
                                Configuration = "{ \"register\": 67, \"registerType\": 3 }"
                            },
                            new MappingConfiguration
                            {
                                Name = "Battery-SOC",
                                Configuration = "{ \"register\": 82, \"registerType\": 3 }"
                            },
                            new MappingConfiguration
                            {
                                Name = "Power Consumption",
                                Configuration = "{ \"register\": 71, \"registerType\": 3 }"
                            },
                            new MappingConfiguration
                            {
                                Name = "Net Consumption",
                                Configuration = "{ \"register\": 73, \"registerType\": 3 }"
                            }
                        }
                    }
                }
            },
            new ServerConfiguration
            {
                Server = "192.168.13.30", Groups = new[]
                {
                    new GroupConfiguration
                    {
                        Name = "General",
                        Id = new Guid("{2E902575-E892-402C-BCC1-DB4F433556EC}"),
                        Mappings = new[]
                        {
                            new MappingConfiguration
                            {
                                Name = "Outside temperature (AZ30-BT23)",
                                Configuration = "{ \"register\": 107, \"registerType\": 4 }"
                            },
                            new MappingConfiguration
                            {
                                Name = "Outside temperature",
                                Configuration = "{\"register\": 1, \"registerType\": 4 }"
                            }
                        }
                    }
                }
            }
        }
    };
}