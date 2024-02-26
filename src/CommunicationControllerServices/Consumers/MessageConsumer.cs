using System.Text.Json;
using System.Text.Json.Serialization;
using Json.More;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Common.DistributionEventHub.Consumers;
using Meshmakers.Octo.Communication.Contracts.MessageObjects;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Consumers;

/// <summary>
/// Message consumer for communication controller
/// </summary>
internal class MessageConsumer : IDistributedConsumer<UpdatedValueMessageDto>
{
    readonly ILogger<MessageConsumer> _logger;
    private readonly ISystemContext _systemContext;
    private readonly IAdapterService _adapterService;
    private readonly IEtlDataOrchestrator _etlDataOrchestrator;
    private readonly Dictionary<string, object?> _etlContextDictionary = new();
    private readonly JsonSerializerOptions _options;

    /// <summary>
    /// Constructor
    /// </summary>
    /// <param name="logger"></param>
    /// <param name="systemContext"></param>
    /// <param name="adapterService"></param>
    /// <param name="etlDataOrchestrator"></param>
    public MessageConsumer(ILogger<MessageConsumer> logger, ISystemContext systemContext, IAdapterService adapterService,
        IEtlDataOrchestrator etlDataOrchestrator)
    {
        _logger = logger;
        _systemContext = systemContext;
        _adapterService = adapterService;
        _etlDataOrchestrator = etlDataOrchestrator;
        
        _options = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };
    }

    /// <inheritdoc />
    public async Task ConsumeAsync(IDistributedContext<UpdatedValueMessageDto> context)
    {
        _logger.LogDebug("[{TenantId}] Received Input: DataPipelineRtId '{DataPipelineRtId}', Value '{Value}'",
            context.Message.TenantId, context.Message.DataPipelineRtId, context.Message.Value);

        var message = context.Message;

        try
        {
            var tenantRepository = await _systemContext.FindTenantRepositoryAsync(message.TenantId.NormalizeString());

            if (message.Value == null)
            {
                _logger.LogWarning("Value is null, skipping");
                return;
            }
            
            using var session = await tenantRepository.GetSessionAsync();
            session.StartTransaction();

            _logger.LogInformation("Execute pipeline {Id}: {Name}", context.Message.DataPipelineRtId, "unknown");
            await _etlDataOrchestrator.ExecutePipelineAsync<IRetrieverEtlContext>(TestConfig.Test1,
                new RetrieverEtlContext(message.TenantId.NormalizeString(), message.Value, tenantRepository, session, _etlContextDictionary));

            await session.CommitTransactionAsync();
        }
        catch (Exception e)
        {
            _logger.LogError(e, "[{TenantId}] Failed to update pipeline '{DataPipelineRtId}'", message.TenantId, message.DataPipelineRtId);
        }
    }
}