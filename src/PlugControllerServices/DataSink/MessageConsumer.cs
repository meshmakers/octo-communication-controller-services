using MassTransit;
using Meshmakers.Octo.Backend.PlugControllerServices.CkModelEntities;
using Meshmakers.Octo.Backend.PlugControllerServices.Services;
using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.Communication.Plugs.Contracts.MessageObjects;
using Meshmakers.Octo.SystematizedData.Persistence;
using Meshmakers.Octo.SystematizedData.Persistence.DataAccess;

namespace Meshmakers.Octo.Backend.PlugControllerServices.DataSink;

public class MessageConsumer :
    IConsumer<UpdatedValueMessageDto>
{
    readonly ILogger<MessageConsumer> _logger;
    private readonly ISystemContext _systemContext;
    private readonly IPlugService _plugService;

    public MessageConsumer(ILogger<MessageConsumer> logger, ISystemContext systemContext, IPlugService plugService)
    {
        _logger = logger;
        _systemContext = systemContext;
        _plugService = plugService;
    }

    public async Task Consume(ConsumeContext<UpdatedValueMessageDto> context)
    {
        _logger.LogInformation("[{TenantId}] Received Input: PlugRtId '{PlugRtId}', Name '{MappingId}', Value '{Value}'",
            context.Message.TenantId, context.Message.PlugId, context.Message.MappingId, context.Message.Value);

        var message = context.Message;
        var config = await _plugService.GetPlugConfiguration(message.TenantId, message.PlugId);
        
        if (config == null)
        {
            _logger.LogWarning("[{TenantId}] Plug '{PlugRtId}' not found", message.TenantId, message.PlugId);
            return;
        }
        
        try
        {

            var tenantContext = await _systemContext.CreateOrGetTenantContextAsync(message.TenantId);

            using var session = await tenantContext.Repository.StartSessionAsync();
            session.StartTransaction();
            
            var plugEntity = await GetMappingAsync(session, tenantContext, message.MappingId);

            if (!string.IsNullOrWhiteSpace(plugEntity.ReferenceId) && !string.IsNullOrWhiteSpace(plugEntity.ReferenceCkId) && !string.IsNullOrWhiteSpace(plugEntity.ReferenceAttributeId))
            {
                var asset = await tenantContext.Repository.GetRtEntityAsync(session, new RtEntityId(plugEntity.ReferenceCkId, new OctoObjectId(plugEntity.ReferenceId)));
                if (asset == null)
                {
                    throw new Exception($"Asset {plugEntity.ReferenceCkId} {plugEntity.ReferenceId} not found");
                }

                var item = tenantContext.CkCache.GetEntityCacheItem(asset.CkId);
                var x = item.Attributes.SingleOrDefault(a => a.Value.AttributeId == plugEntity.ReferenceAttributeId);
                if (x.Value == null)
                {
                    throw new Exception($"Attribute {plugEntity.ReferenceAttributeId} not found");
                }
                asset.SetAttributeValue(x.Value.AttributeName, x.Value.AttributeValueType, message.Value);
                
                await tenantContext.Repository.ApplyChanges(session, new[] { new EntityUpdateInfo(asset, EntityModOptions.Update) });
            }
            
            await session.CommitTransactionAsync();
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
            throw;
        }

    }
    
    private async Task<RtPlugMapping> GetMappingAsync(IOctoSession systemSession, ITenantContext tenantContext,
        OctoObjectId mappingObjectId)
    {
        var plugMapping =
            await tenantContext.Repository.GetRtEntityAsync<RtPlugMapping>(systemSession, new RtEntityId(Statics.CkIdPlugMapping, mappingObjectId));
        if (plugMapping == null)
        {
            throw new Exception($"Plug mapping {mappingObjectId} not found");
        }

        return plugMapping;
    }
}