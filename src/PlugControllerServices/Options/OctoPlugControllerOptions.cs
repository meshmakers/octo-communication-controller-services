namespace Meshmakers.Octo.Backend.PlugControllerServices.Options;

/// <summary>
/// General Device Management initialization options
/// </summary>
public class OctoPlugControllerOptions
{
    /// <summary>
    /// Constructor
    /// </summary>
    public OctoPlugControllerOptions()
    {
        Authority = "https://localhost:5003";
        RedisCacheHost = "localhost";
    }

    /// <summary>
    ///     (Public) base address of the CAS (Central Authorization Services)
    /// </summary>
    public string Authority { get; set; }
    
    /// <summary>
    ///     Gets or sets the redis cache host name
    /// </summary>
    public string RedisCacheHost { get; set; }

    /// <summary>
    ///     Gets or sets the redis cache password
    /// </summary>
    public string? RedisCachePassword { get; set; }
}