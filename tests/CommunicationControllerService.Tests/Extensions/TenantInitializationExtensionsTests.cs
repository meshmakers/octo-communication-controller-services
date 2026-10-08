using Meshmakers.Octo.Backend.CommunicationControllerServices.Extensions;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Messages;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;

using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Extensions;

/// <summary>
///     CK v2 F1.0 (AB#5900): the tenant initialization accepts the embedded CK model version OR A NEWER ONE. Since the
///     engine's embedded-import downgrade guard a tenant may have a newer System / System.Bot / System.Communication
///     than this service embeds; an exact existence check would treat that as missing.
/// </summary>
internal class TenantInitializationExtensionsTests
{
    private static readonly CkModelId Embedded = SystemCommunicationCkIds.CkModelId;

    [Test]
    public async Task EnsureCkModel_EmbeddedVersionOrNewerInstalled_DoesNotImport()
    {
        var tenantContext = Substitute.For<ITenantContext>();
        tenantContext.IsCkModelSatisfiedAsync(Embedded).Returns(true);

        await TenantInitializationExtensions.EnsureCkModelAsync(tenantContext, Embedded);

        await tenantContext.DidNotReceive().ImportCkModelAsync(Arg.Any<CkModelId>(), Arg.Any<OperationResult>());
        await tenantContext.DidNotReceive().IsCkModelExistingAsync(Arg.Any<CkModelId>());
    }

    [Test]
    public async Task EnsureCkModel_NotSatisfied_ImportsTheEmbeddedVersion()
    {
        var tenantContext = Substitute.For<ITenantContext>();
        tenantContext.IsCkModelSatisfiedAsync(Embedded).Returns(false);

        await TenantInitializationExtensions.EnsureCkModelAsync(tenantContext, Embedded);

        await tenantContext.Received(1).ImportCkModelAsync(Embedded, Arg.Any<OperationResult>());
    }

    [Test]
    public async Task EnsureCkModel_ImportReportsErrors_Throws()
    {
        var tenantContext = Substitute.For<ITenantContext>();
        tenantContext.IsCkModelSatisfiedAsync(Embedded).Returns(false);
        tenantContext.ImportCkModelAsync(Embedded, Arg.Do<OperationResult>(r =>
                r.AddMessage(new OperationMessage(MessageLevel.Error, null, 1, "boom"))))
            .Returns(Task.CompletedTask);

        await Assert.That(() => TenantInitializationExtensions.EnsureCkModelAsync(tenantContext, Embedded))
            .Throws<InvalidOperationException>();
    }

    /// <summary>A skip by the engine guard (warning only) is not an error.</summary>
    [Test]
    public async Task EnsureCkModel_ImportReportsOnlyAWarning_DoesNotThrow()
    {
        var tenantContext = Substitute.For<ITenantContext>();
        tenantContext.IsCkModelSatisfiedAsync(Embedded).Returns(false);
        tenantContext.ImportCkModelAsync(Embedded, Arg.Do<OperationResult>(r =>
                r.AddMessage(new OperationMessage(MessageLevel.Warning, null, 0, "skipped"))))
            .Returns(Task.CompletedTask);

        await TenantInitializationExtensions.EnsureCkModelAsync(tenantContext, Embedded);
    }
}
