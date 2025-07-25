using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;

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
        PublicUrl = "https://localhost:5015";
        AuthorityUrl = "https://localhost:5003";

        BrokerHost = "localhost";
        BrokerVirtualHost = "/";
        BrokerPort = 5672;
        BrokerUser = "guest";
        BrokerPassword = "guest";
#if DEBUGL || DEBUG
        MinLogLevel = LogLevelDto.Trace;
#else
        MinLogLevel = LogLevelDto.Warn;
#endif
    }
    
    /// <summary>
    ///    (Public) base address of the service
    /// </summary>
    public string PublicUrl { get; set; }

    /// <summary>
    ///     (Public) base address of the CAS (Central Authorization Services)
    /// </summary>
    public string AuthorityUrl { get; set; }

    /// <summary>
    ///     Gets or sets the prefix for the OctoMesh installation instance.
    /// </summary>
    public string? InstancePrefix { get; set; }

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
    public string? BrokerUser { get; set; }

    /// <summary>
    /// Gets or sets the RabbitMQ broker password
    /// </summary>
    public string? BrokerPassword { get; set; }

    /// <summary>
    /// Gets or sets the minimal log level to be logged
    /// </summary>
    public LogLevelDto MinLogLevel { get; set; }
}