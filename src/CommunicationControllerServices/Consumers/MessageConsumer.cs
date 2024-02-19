using Meshmakers.Common.Shared;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Common.DistributionEventHub.Consumers;
using Meshmakers.Octo.Communication.Contracts.MessageObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.ConstructionKit.Generated.System.Communication.v1;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repository;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;

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
            var tenantRepository = await _systemContext.FindTenantRepositoryAsync(message.TenantId.NormalizeString());

            using var session = await tenantRepository.GetSessionAsync();
            session.StartTransaction();

            var streamAssociation = await GetStreamAssoc(session, tenantRepository, message.MappingId);

            if (streamAssociation != null && (streamAssociation.TargetCkAttributeIds?.Any() ?? false))
            {
                var rtEntityId = new RtEntityId(streamAssociation.TargetCkTypeId, streamAssociation.TargetRtId);
                var asset = await tenantRepository.GetRtEntityByRtIdAsync(session, rtEntityId);
                if (asset == null)
                {
                    throw new Exception($"Asset {streamAssociation.TargetCkTypeId} {streamAssociation.TargetRtId} not found");
                }
                
                var ckTypeGraph = await tenantRepository.GetCkTypeGraphAsync(asset.GetCkTypeId());
                if (!ckTypeGraph.AllAttributes.TryGetValue(streamAssociation.TargetCkAttributeIds.First(), out var ckTypeAttributeGraph))
                {
                    throw new Exception($"Attribute {streamAssociation.TargetCkAttributeIds.First()} not found");
                }
                asset.SetAttributeValue(ckTypeAttributeGraph.AttributeName, ckTypeAttributeGraph.ValueType, message.Value);

                OperationResult operationResult = new();
                await tenantRepository.ApplyChangesAsync(session, new[] { EntityUpdateInfo<RtEntity>.CreateUpdate(rtEntityId, asset)}, operationResult);
                if (operationResult.HasFatalErrors || operationResult.HasErrors)
                {
                    throw new Exception($"Failed to update asset {streamAssociation.TargetCkTypeId} {streamAssociation.TargetRtId}");
                }
            }
            

            await session.CommitTransactionAsync();
        }
        catch (Exception e)
        {
            _logger.LogError(e, "[{TenantId}] Failed to update plug '{PlugRtId}'", message.TenantId, message.PlugRtId);
        }
    }

    private async Task<RtAssociation?> GetStreamAssoc(IOctoSession session, ITenantRepository tenantRepository,
        OctoObjectId mappingObjectId)
    {
        var resultSet =
            await tenantRepository.GetRtAssociationsAsync(session,
                new[] { mappingObjectId }, GraphDirections.Outbound,
                new CkId<CkAssociationRoleId>(SystemCommunicationCkIds.ModelId, SystemCommunicationCkIds.Stream));

        return resultSet.FirstOrDefault();
    }
}