using Meshmakers.Octo.Backend.CommunicationControllerServices.Methods.Service;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Models;
using Meshmakers.Octo.Backend.CommunicationControllerServices.TenantApi.v1.Controllers;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Methods.Service;

/// <summary>
///     AB#8596 — the helpers the Pipeline / workload methods share with the REST controllers: the redaction of sensitive
///     parameters in failure messages, and the audit texts (the REST texts are pinned, the extraction must not change them).
/// </summary>
internal class PipelineMethodSupportTests
{
    private const string Definition = """
                                      triggers:
                                        - type: FromHttpRequest@1
                                      transformations:
                                        - type: HttpRequest@1
                                          password: s3cr3t-Value-42
                                      """;

    [Test]
    public async Task Redact_MasksTheWholeValue_EveryLongLine_AndTheToken()
    {
        var message = "Definition rejected: 'password: s3cr3t-Value-42' is not allowed; token eyJ.token.sig; full: " +
                      Definition;

        var redacted = CommunicationMethodSupport.Redact(message, Definition, "eyJ.token.sig");

        await Assert.That(redacted).DoesNotContain("s3cr3t-Value-42");
        await Assert.That(redacted).DoesNotContain("eyJ.token.sig");
        await Assert.That(redacted).StartsWith("Definition rejected: '***' is not allowed");
    }

    [Test]
    public async Task Redact_LeavesShortLinesAndAMessageWithoutValuesAlone()
    {
        await Assert.That(CommunicationMethodSupport.Redact("Adapter offline", "a: b\nc", null))
            .IsEqualTo("Adapter offline");
        await Assert.That(CommunicationMethodSupport.Redact(null, "x")).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task ChartVersionAuditMessage_KeepsTheRestText()
    {
        var id = OctoObjectId.Parse("65a000000000000000000001");

        await Assert.That(WorkloadController.BuildChartVersionAuditMessage(id, null, "1.2.3", "source: CI/CD"))
            .IsEqualTo($"Chart version for workload {id} set to 1.2.3 (source: CI/CD).");
        await Assert.That(WorkloadController.BuildChartVersionAuditMessage(id, "", "", "source: CI/CD"))
            .IsEqualTo($"Chart version for workload {id} updated from (latest) to (latest) (source: CI/CD).");
        await Assert.That(WorkloadController.BuildChartVersionAuditMessage(id, "1.0.0", "1.2.3",
                DeployableWorkloadSetChartVersionHandler.ChartVersionSource))
            .IsEqualTo($"Chart version for workload {id} updated from 1.0.0 to 1.2.3 (source: CI/CD via CK method).");
    }

    [Test]
    public async Task DebugAndMoveAuditMessages_KeepTheRestText()
    {
        var pipeline = OctoObjectId.Parse("65a000000000000000000002");
        var oldAdapter = new RtEntityId(SystemCommunicationCkIds.RtCkAdapterTypeId,
            OctoObjectId.Parse("65a000000000000000000003"));
        var newAdapter = new RtEntityId(SystemCommunicationCkIds.RtCkAdapterTypeId,
            OctoObjectId.Parse("65a000000000000000000004"));

        await Assert.That(PipelineController.BuildDebugAuditMessage(pipeline, true, true, "source: User"))
            .IsEqualTo($"Pipeline {pipeline} debugging enabled and applied to the running adapter (source: User).");
        await Assert.That(PipelineController.BuildDebugAuditMessage(pipeline, false, false, "source: User"))
            .IsEqualTo($"Pipeline {pipeline} debugging disabled; adapter offline, will apply on next deploy (source: User).");
        await Assert.That(PipelineController.BuildMoveAuditMessage(pipeline,
                new PipelineMoveResult(pipeline, oldAdapter, newAdapter), "source: User"))
            .IsEqualTo($"Pipeline {pipeline} moved from adapter {oldAdapter.RtId} to {newAdapter.RtId} (source: User).");
        await Assert.That(PipelineController.BuildMoveAuditMessage(pipeline,
                new PipelineMoveResult(pipeline, newAdapter, newAdapter), "source: User"))
            .IsEqualTo(
                $"Pipeline {pipeline} move requested but already pointed at adapter {newAdapter.RtId} (source: User, no-op).");
    }
}
