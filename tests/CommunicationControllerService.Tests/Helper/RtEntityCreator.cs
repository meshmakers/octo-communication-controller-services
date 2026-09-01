using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v3;
using Meshmakers.Octo.Runtime.Contracts;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;

internal static class RtEntityCreator
{
    /// <summary>
    /// Creates an RtEntityId from an RtAdapter
    /// </summary>
    public static RtEntityId ToRtEntityId(this RtAdapter entity)
    {
        return new RtEntityId(entity.CkTypeId!, entity.RtId);
    }

    /// <summary>
    /// Creates an RtEntityId from an RtPipeline
    /// </summary>
    public static RtEntityId ToRtEntityId(this RtPipeline entity)
    {
        return new RtEntityId(entity.CkTypeId!, entity.RtId);
    }

    /// <summary>
    /// Creates an RtEntityId from an RtPipelineExecution
    /// </summary>
    public static RtEntityId ToRtEntityId(this RtPipelineExecution entity)
    {
        return new RtEntityId(entity.CkTypeId!, entity.RtId);
    }

    /// <summary>
    /// Creates an RtEntityId from an RtPipelineStatistics
    /// </summary>
    public static RtEntityId ToRtEntityId(this RtPipelineStatistics entity)
    {
        return new RtEntityId(entity.CkTypeId!, entity.RtId);
    }

    /// <summary>
    /// Creates an RtEntityId from an RtDataFlow
    /// </summary>
    public static RtEntityId ToRtEntityId(this RtDataFlow entity)
    {
        return new RtEntityId(entity.CkTypeId!, entity.RtId);
    }

    public static RtDataFlow CreateDataFlow(string? id = null)
    {
        id ??= OctoObjectId.GenerateNewId().ToString();
        return new RtDataFlow
        {
            RtId = new OctoObjectId(id),
            CkTypeId = SystemCommunicationCkIds.RtCkDataFlowTypeId
        };
    }

    public static RtPipeline CreatePipeline(string? pipelineDefinition = null, string? id = null)
    {
        id = id ?? OctoObjectId.GenerateNewId().ToString();
        return new RtPipeline
        {
            RtId = new OctoObjectId(id),
            CkTypeId = SystemCommunicationCkIds.RtCkPipelineTypeId,
            PipelineDefinition = pipelineDefinition ?? "pipelineDefinition",
            DeploymentState = RtDeploymentStateEnum.Deployed
        };
    }

    public static RtPipeline CreatePipelineFrom(RtPipeline source)
    {
        return new RtPipeline
        {
            RtId = source.RtId,
            CkTypeId = source.CkTypeId,
            PipelineDefinition = source.PipelineDefinition,
            DeploymentState = source.DeploymentState,
            IsDebuggingEnabled = source.IsDebuggingEnabled
        };
    }

    public static RtAdapter CreateAdapter(string? id = null)
    {
        id = id ?? OctoObjectId.GenerateNewId().ToString();
        return new RtAdapter
        {
            RtId = new OctoObjectId(id),
            CkTypeId = SystemCommunicationCkIds.RtCkAdapterTypeId
        };
    }

    /// <summary>
    /// AB#5027: a ServiceAccountConfiguration usable both as an adapter-wide default and as a
    /// per-pipeline override. Both <c>CkTypeId</c> and <c>RtWellKnownName</c> must be set —
    /// <c>AdapterService.CreatePipelineConfigurationAsync</c> throws on either being null.
    /// </summary>
    public static RtServiceAccountConfiguration CreateServiceAccountConfiguration(string? wellKnownName = null,
        string? id = null)
    {
        id ??= OctoObjectId.GenerateNewId().ToString();
        return new RtServiceAccountConfiguration
        {
            RtId = new OctoObjectId(id),
            CkTypeId = SystemCommunicationCkIds.RtCkServiceAccountConfigurationTypeId,
            RtWellKnownName = wellKnownName ?? "adapter-service-account",
            // All four attributes are mandatory on the CK type — the generated getters throw
            // InvalidAttributeValueException on null, and Serialize() reads every one of them.
            ClientId = "client-id",
            ClientSecret = "client-secret",
            IssuerUri = "https://identity.example.com",
            TenantId = "tenantId"
        };
    }

    public static RtPipelineExecution CreatePipelineExecution(
        string? executionId = null,
        RtPipelineExecutionStatusEnum status = RtPipelineExecutionStatusEnum.Running,
        RtPipelineTriggerTypeEnum triggerType = RtPipelineTriggerTypeEnum.Manual,
        string? id = null)
    {
        id = id ?? OctoObjectId.GenerateNewId().ToString();
        executionId = executionId ?? Guid.NewGuid().ToString();
        return new RtPipelineExecution
        {
            RtId = new OctoObjectId(id),
            CkTypeId = SystemCommunicationCkIds.RtCkPipelineExecutionTypeId,
            ExecutionId = executionId,
            Status = status,
            TriggerType = triggerType,
            StartedAt = DateTime.UtcNow
        };
    }

    public static RtPipelineStatistics CreatePipelineStatistics(string? id = null)
    {
        id = id ?? OctoObjectId.GenerateNewId().ToString();
        return new RtPipelineStatistics
        {
            RtId = new OctoObjectId(id),
            CkTypeId = SystemCommunicationCkIds.RtCkPipelineStatisticsTypeId,
            LastHourSuccessCount = 0,
            LastHourFailureCount = 0,
            LastHourAvgDurationMs = 0,
            Last12HoursSuccessCount = 0,
            Last12HoursFailureCount = 0,
            Last12HoursAvgDurationMs = 0,
            Last24HoursSuccessCount = 0,
            Last24HoursFailureCount = 0,
            Last24HoursAvgDurationMs = 0,
            Last30DaysSuccessCount = 0,
            Last30DaysFailureCount = 0,
            Last30DaysAvgDurationMs = 0,
            LastUpdatedAt = DateTime.UtcNow
        };
    }

    public static RtPipelineTrigger CreatePipelineTrigger(string? cronExpression = null,
        string? name = null, string? id = null)
    {
        id ??= OctoObjectId.GenerateNewId().ToString();
        return new RtPipelineTrigger
        {
            RtId = new OctoObjectId(id),
            CkTypeId = SystemCommunicationCkIds.RtCkPipelineTriggerTypeId,
            Enabled = true,
            CronExpression = cronExpression ?? "0 * * * *",
            Name = name ?? "Test Trigger"
        };
    }

    public static RtEntityId ToRtEntityId(this RtPipelineTrigger entity)
    {
        return new RtEntityId(entity.CkTypeId!, entity.RtId);
    }

}
