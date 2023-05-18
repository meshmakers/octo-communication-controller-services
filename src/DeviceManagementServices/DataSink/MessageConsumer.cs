using MassTransit;
using Meshmakers.Octo.Communication.Plugs.Contracts.Data;

namespace Meshmakers.Octo.Backend.DeviceManagementServices.DataSink;

public class MessageConsumer :
    IConsumer<UpdatedValueMessage>
{
    readonly ILogger<MessageConsumer> _logger;

    public MessageConsumer(ILogger<MessageConsumer> logger)
    {
        _logger = logger;
    }

    public Task Consume(ConsumeContext<UpdatedValueMessage> context)
    {
        _logger.LogInformation("Received Input: Server '{PlugId}', Group '{Group}', Name '{Name}', Value '{Value}'",
            context.Message.PlugId, context.Message.Group, context.Message.Name, context.Message.Value);

        return Task.CompletedTask;
    }
}