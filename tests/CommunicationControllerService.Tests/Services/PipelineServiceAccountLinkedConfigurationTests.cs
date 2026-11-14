using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Communication.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services;

/// <summary>
/// AB#8982 — the automatic reconcile paths also converge the standalone service accounts that
/// pipelines link via <c>Uses</c> (per-pipeline overrides a blueprint ships, e.g.
/// <c>accounting-ai-prompt</c>). Before, only the adapters' own accounts were reconciled and
/// <c>DeployDataFlow</c> refused those pipelines with "identity client '' does not exist" until
/// someone called <c>POST serviceAccount/reconcile</c> by hand.
/// </summary>
internal class PipelineServiceAccountLinkedConfigurationTests
{
    private const string TenantId = UsesLinkedServiceAccountFixture.TenantId;

    private readonly UsesLinkedServiceAccountFixture _fixture = new();

    [Test]
    public async Task EnsureTenantProvisioned_BlueprintShippedAccount_GetsAClientWithItsDeclaredRoles()
    {
        var aiPrompt = _fixture.AddStandalone("accounting-ai-prompt", ["AccountingRead"]);
        var sut = _fixture.CreateProvisioningService();

        var report = await sut.EnsureTenantProvisionedAsync(TenantId);

        var expectedClientId = PipelineServiceAccountProvisioningService.BuildClientId(aiPrompt.RtId);
        var sent = _fixture.SentClients().Single(c => c.ClientId == expectedClientId);
        var written = _fixture.UpdatedStandalone().Single();

        using var _ = Assert.Multiple();
        await Assert.That(report.HasFailures).IsFalse();
        // The adapter account is steady, the blueprint account is new.
        await Assert.That(report.AlreadyProvisioned).IsEqualTo(1);
        await Assert.That(report.Provisioned + report.Repaired).IsEqualTo(1);
        await Assert.That(sent.AssignedRoleNames)
            .IsEquivalentTo(new[] { "AccountingRead", CommonConstants.StreamDataReaderRole });
        // The adapter's own client (reconciled first) may act as the account.
        await Assert.That(sent.MayActAsClientIds!).IsEquivalentTo(new[] { _fixture.AdapterClientId });
        // The client id lands on the entity — DeployDataFlow resolves the account by it.
        await Assert.That(written.RtId).IsEqualTo(aiPrompt.RtId);
        await Assert.That(written.GetAttributeValueOrDefault(nameof(RtServiceAccountConfiguration.ClientId)))
            .IsEqualTo(expectedClientId);
    }

    [Test]
    public async Task EnsureTenantProvisioned_AdapterAccountIsReconciledBeforeTheLinkedOnes()
    {
        _fixture.AddStandalone("accounting-ai-prompt", ["AccountingRead"]);
        var sut = _fixture.CreateProvisioningService();

        await sut.EnsureTenantProvisionedAsync(TenantId);

        // The linked account names the adapter's client as its MayActAs actor, so that client must
        // already exist identity-side.
        await Assert.That(_fixture.SentClients()[0].ClientId).IsEqualTo(_fixture.AdapterClientId);
    }

    [Test]
    public async Task EnsureTenantProvisioned_UpdatedDeclaration_IsMaterialisedWithoutAManualReconcile()
    {
        // A blueprint update changed the declared roles of an account that already has its client.
        var reader = _fixture.AddStandalone("accounting-pipeline-reader", ["AccountingRead", "AccountingWrite"],
            clientId: "octo-pipeline-sa-reader");
        reader.ClientSecret = SecretTestValues.Stored("reader-secret");
        var sut = _fixture.CreateProvisioningService();

        var report = await sut.EnsureTenantProvisionedAsync(TenantId);

        var sent = _fixture.SentClients().Single(c => c.ClientId == "octo-pipeline-sa-reader");
        using var _ = Assert.Multiple();
        await Assert.That(report.HasFailures).IsFalse();
        await Assert.That(sent.AssignedRoleNames)
            .IsEquivalentTo(new[] { "AccountingRead", "AccountingWrite", CommonConstants.StreamDataReaderRole });
    }

    [Test]
    public async Task EnsureTenantProvisioned_UnusedAndAdapterOwnedAccounts_AreNotReconciledAsStandalone()
    {
        _fixture.AddStandalone("orphan", ["AccountingRead"], linked: false);
        var sut = _fixture.CreateProvisioningService();

        await sut.EnsureTenantProvisionedAsync(TenantId);

        using var _ = Assert.Multiple();
        // Only the adapter's own account went to identity — once, through the adapter path.
        await Assert.That(_fixture.SentClients().Select(c => c.ClientId))
            .IsEquivalentTo(new[] { _fixture.AdapterClientId });
        await Assert.That(_fixture.UpdatedStandalone().Count).IsEqualTo(0);
    }

    [Test]
    public async Task EnsureTenantProvisioned_NoAdapters_StillReconcilesLinkedAccounts()
    {
        // The early "no adapters" exit is gone: a linked account is reconciled even when the adapter
        // list is empty (its actor list then stays null and it keeps a secret of its own).
        _fixture.Repository.GetAdaptersAsync(TenantId).Returns([]);
        var aiPrompt = _fixture.AddStandalone("accounting-ai-prompt", ["AccountingRead"]);
        var sut = _fixture.CreateProvisioningService();

        var report = await sut.EnsureTenantProvisionedAsync(TenantId);

        using var _ = Assert.Multiple();
        await Assert.That(report.Provisioned + report.Repaired).IsEqualTo(1);
        await Assert.That(_fixture.UpdatedStandalone().Single().RtId).IsEqualTo(aiPrompt.RtId);
    }

    [Test]
    public async Task EnsureLinkedConfigurations_OneAccountFails_TheOthersAreStillReconciled()
    {
        var broken = _fixture.AddStandalone("broken-account", ["AccountingRead"]);
        var healthy = _fixture.AddStandalone("accounting-ai-prompt", ["AccountingRead"]);
        _fixture.Repository.UpdateServiceAccountAsync(TenantId,
                Arg.Is<RtServiceAccountConfiguration>(c => c.RtId == broken.RtId))
            .ThrowsAsync(new InvalidOperationException("write failed"));
        var sut = _fixture.CreateProvisioningService();

        var report = await sut.EnsureLinkedConfigurationsProvisionedAsync(TenantId);

        using var _ = Assert.Multiple();
        await Assert.That(report.Failures.Count).IsEqualTo(1);
        await Assert.That(report.Failures[0]).Contains("broken-account");
        await Assert.That(report.Provisioned + report.Repaired).IsEqualTo(1);
        await Assert.That(_fixture.UpdatedStandalone().Any(c => c.RtId == healthy.RtId)).IsTrue();
        await _fixture.EventService.Received(1).StoreErrorEventAsync(TenantId,
            Arg.Is<string>(m => m.Contains("broken-account") && m.Contains("AB#8982")),
            Arg.Any<RtEntityId?>());
    }

    [Test]
    public async Task EnsureLinkedConfigurations_LookupFails_ReportsInsteadOfThrowing()
    {
        _fixture.Repository.GetServiceAccountsAsync(TenantId)
            .ThrowsAsync(new InvalidOperationException("ck cache unloaded"));
        var sut = _fixture.CreateProvisioningService();

        var report = await sut.EnsureLinkedConfigurationsProvisionedAsync(TenantId);

        using var _ = Assert.Multiple();
        await Assert.That(report.HasFailures).IsTrue();
        await Assert.That(report.HasChanges).IsFalse();
    }

    [Test]
    public async Task EnsureLinkedConfigurations_ForAnAdapter_OnlyReconcilesTheAccountsOfItsPipelines()
    {
        var otherAdapter = RtEntityCreator.CreateAdapter();
        var mine = _fixture.AddStandalone("accounting-ai-prompt", ["AccountingRead"]);
        _fixture.AddStandalone("someone-elses", ["AccountingRead"], runsOn: otherAdapter);
        var sut = _fixture.CreateProvisioningService();

        await sut.EnsureLinkedConfigurationsProvisionedAsync(TenantId, _fixture.Adapter);

        await Assert.That(_fixture.UpdatedStandalone().Select(c => c.RtId)).IsEquivalentTo(new[] { mine.RtId });
    }
}
