using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Common.DistributionEventHub.Consumers;
using Meshmakers.Octo.Communication.Contracts.MessageObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.ConstructionKit.Generated.System.Communication.v1;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Consumers;

/// <summary>
/// Message consumer for communication controller
/// </summary>
internal class MessageConsumer : IDistributedConsumer<UpdatedValueMessageDto>
{
    readonly ILogger<MessageConsumer> _logger;
    private readonly ISystemContext _systemContext;
    private readonly IPlugService _plugService;

    /// <summary>
    /// Constructor
    /// </summary>
    /// <param name="logger"></param>
    /// <param name="systemContext"></param>
    /// <param name="plugService"></param>
    public MessageConsumer(ILogger<MessageConsumer> logger, ISystemContext systemContext, IPlugService plugService)
    {
        _logger = logger;
        _systemContext = systemContext;
        _plugService = plugService;
    }

    /// <inheritdoc />
    public async Task ConsumeAsync(IDistributedContext<UpdatedValueMessageDto> context)
    {
        _logger.LogInformation("[{TenantId}] Received Input: PlugRtId '{PlugRtId}', Name '{MappingId}', Value '{Value}'",
            context.Message.TenantId, context.Message.PlugRtId, context.Message.MappingId, context.Message.Value);

        var message = context.Message;
        
        try
        {
            var config = await _plugService.GetPlugConfigurationAsync(message.TenantId, message.PlugRtId);


            var tenantContext = await _systemContext.GetChildTenantContextAsync(message.TenantId);
            var tenantRepository = tenantContext.GetTenantRepository();

            using var session = await tenantRepository.GetSessionAsync();
            session.StartTransaction();
            
            var plugEntity = await GetMappingAsync(session, tenantContext, message.MappingId);

            // if (!string.IsNullOrWhiteSpace(plugEntity.ReferenceId) && !string.IsNullOrWhiteSpace(plugEntity.ReferenceCkId) && !string.IsNullOrWhiteSpace(plugEntity.ReferenceAttributeId))
            // {
            //     var asset = await tenantRepository.GetRtEntityByRtIdAsync(session, new RtEntityId(plugEntity.ReferenceCkId, new OctoObjectId(plugEntity.ReferenceId)));
            //     if (asset == null)
            //     {
            //         throw new Exception($"Asset {plugEntity.ReferenceCkId} {plugEntity.ReferenceId} not found");
            //     }
            //
            //     var item = tenantContext.CkCache.GetEntityCacheItem(asset.CkId);
            //     var x = item.Attributes.SingleOrDefault(a => a.Value.AttributeId == plugEntity.ReferenceAttributeId);
            //     if (x.Value == null)
            //     {
            //         throw new Exception($"Attribute {plugEntity.ReferenceAttributeId} not found");
            //     }
            //     asset.SetAttributeValue(x.Value.AttributeName, x.Value.AttributeValueType, message.Value);
            //     
            //     await tenantRepository.ApplyChangesAsync(session, new[] { new EntityUpdateInfo(asset, EntityModOptions.Update) });
            // }
            
            await session.CommitTransactionAsync();
        }
        catch (Exception e)
        {
            _logger.LogError(e, "[{TenantId}] Failed to update plug '{PlugRtId}'", message.TenantId, message.PlugRtId);
        }

    }
    
    private async Task<RtPlugMapping> GetMappingAsync(IOctoSession systemSession, ITenantContext tenantContext,
        OctoObjectId mappingObjectId)
    {
        var plugMapping =
            await tenantContext.GetTenantRepository().GetRtEntityByRtIdAsync<RtPlugMapping>(systemSession, mappingObjectId);
        if (plugMapping == null)
        {
            throw new Exception($"Plug mapping {mappingObjectId} not found");
        }

        return plugMapping;
    }
}