namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Options;

/// <summary>
/// General Device Management initialization options
/// </summary>
public class CommunicationControllerOptions
{
    /// <summary>
    /// Constructor
    /// </summary>
    public CommunicationControllerOptions()
    {
        Authority = "https://localhost:5003";
        RedisCacheHost = "localhost";
        
        BrokerHost = "localhost";
        BrokerVirtualHost = "/";
        BrokerPort = 5672;
        BrokerUsername = "guest";
        BrokerPassword = "guest";
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
    
    /// <summary>
    /// Gets or sets the RabbitMQ broker host name
    /// </summary>
    public string BrokerHost { get; set; }
    
    /// <summary>
    /// Gets or sets the RabbitMQ broker virtual host
    /// </summary>
    public string BrokerVirtualHost { get; set; }
    
    /// <summary>
    /// Gets or sets the RabbitMQ broker port
    /// </summary>
    public ushort BrokerPort { get; set; }
    
    /// <summary>
    /// Gets or sets the RabbitMQ broker username
    /// </summary>
    public string? BrokerUsername { get; set; }
    
    /// <summary>
    /// Gets or sets the RabbitMQ broker password
    /// </summary>
    public string? BrokerPassword { get; set; }
}