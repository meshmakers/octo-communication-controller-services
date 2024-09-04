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
        Authority = "https://localhost:5003";

        BrokerHost = "localhost";
        BrokerVirtualHost = "/";
        BrokerPort = 5672;
        BrokerUser = "guest";
        BrokerPassword = "guest";
#if DEBUG || DEBUGL
        MinLogLevel = LogLevelDto.Debug;
#else
        MinLogLevel = LogLevelDto.Info;
#endif
    }

    /// <summary>
    ///     (Public) base address of the CAS (Central Authorization Services)
    /// </summary>
    public string Authority { get; set; }


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