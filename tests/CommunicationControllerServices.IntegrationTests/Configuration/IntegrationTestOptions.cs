namespace Meshmakers.Octo.Backend.CommunicationControllerServices.IntegrationTests.Configuration;

/// <summary>
/// Options for integration tests loaded from appsettings.test.json.
/// </summary>
public class IntegrationTestOptions
{
    public string TenantId { get; set; } = null!;

    public string MongoDbImage { get; set; } = "mongo:8.0.15";

    public string AdminUser { get; set; } = "octo-system-admin";

    public string AdminUserPassword { get; set; } = null!;

    public string DatabaseUserPassword { get; set; } = null!;

    public bool UseDirectConnection { get; set; }
}
