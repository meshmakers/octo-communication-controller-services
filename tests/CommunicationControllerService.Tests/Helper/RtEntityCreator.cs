using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v2;
using RtDataFlow = Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v3.RtDataFlow;
using SystemCommunicationV3 = Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v3;
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
            CkTypeId = SystemCommunicationV3.SystemCommunicationCkIds.RtCkDataFlowTypeId
        };
    }

    public static RtPipeline CreatePipeline(string? pipelineDefinition = null, string? id = null)
    {
        id = id ?? OctoObjectId.GenerateNewId().ToString();
        return new RtPipeline
        {
            RtId = new OctoObjectId(id),
            CkTypeId = SystemCommunicationCkIds.RtCkMeshPipelineTypeId,
            PipelineDefinition = pipelineDefinition ?? "pipelineDefinition",
            DeploymentState = RtDeploymentStateEnum.Deployed
        };
    }

    public static RtAdapter CreateAdapter(string? id = null)
    {
        id = id ?? OctoObjectId.GenerateNewId().ToString();
        return new RtAdapter
        {
            RtId = new OctoObjectId(id),
            CkTypeId = SystemCommunicationCkIds.RtCkMeshAdapterTypeId
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

    public static RtDataPipelineTrigger CreateDataPipelineTrigger(string? cronExpression = null,
        string? name = null, string? id = null)
    {
        id ??= OctoObjectId.GenerateNewId().ToString();
        return new RtDataPipelineTrigger
        {
            RtId = new OctoObjectId(id),
            CkTypeId = SystemCommunicationCkIds.RtCkDataPipelineTriggerTypeId,
            Enabled = true,
            CronExpression = cronExpression ?? "0 * * * *",
            Name = name ?? "Test Trigger"
        };
    }

    public static RtEntityId ToRtEntityId(this RtDataPipelineTrigger entity)
    {
        return new RtEntityId(entity.CkTypeId!, entity.RtId);
    }

    public static RtMeshPipeline CreateMeshPipeline(string? pipelineDefinition = null, string? id = null)
    {
        id ??= OctoObjectId.GenerateNewId().ToString();
        return new RtMeshPipeline
        {
            RtId = new OctoObjectId(id),
            CkTypeId = SystemCommunicationCkIds.RtCkMeshPipelineTypeId,
            PipelineDefinition = pipelineDefinition ?? "pipelineDefinition",
            DeploymentState = RtDeploymentStateEnum.Deployed
        };
    }

    public static RtEntityId ToRtEntityId(this RtMeshPipeline entity)
    {
        return new RtEntityId(entity.CkTypeId!, entity.RtId);
    }
}
