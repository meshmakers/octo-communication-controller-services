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

    [Test]
    public async Task EnsureLinkedConfigurations_SecretlessImpersonatingAccount_KeepsTheIdentitySecret()
    {
        // An account between an impersonation switch and its invalidation: stored secret cleared, the
        // identity client still holds the old secret that running pipelines present. The automatic
        // pass must not replace it (the manual reconcile still does, by design).
        var switched = _fixture.AddStandalone("accounting-ai-prompt", ["AccountingRead"],
            clientId: "octo-pipeline-sa-switched");
        var sut = _fixture.CreateProvisioningService();

        await sut.EnsureLinkedConfigurationsProvisionedAsync(TenantId);

        var sent = _fixture.SentClients().Single(c => c.ClientId == "octo-pipeline-sa-switched");
        using var _ = Assert.Multiple();
        await Assert.That(sent.ClientSecret).IsNull();
        await Assert.That(sent.MayActAsClientIds!).IsEquivalentTo(new[] { _fixture.AdapterClientId });
        await Assert.That(_fixture.UpdatedStandalone().Count).IsEqualTo(0);

        // Contrast: the manual endpoint's path sends a throwaway secret (AB#5114 behaviour, unchanged).
        await sut.ReconcileConfigurationAsync(TenantId, switched, ServiceAccountReconcileContext.System);
        await Assert.That(_fixture.SentClients().Last().ClientSecret).IsNotNull();
    }

    [Test]
    public async Task EnsureLinkedConfigurations_ForeignIdentityTarget_IsNotReconciledAutomatically()
    {
        var foreign = _fixture.AddStandalone("cross-tenant-account", ["AccountingRead"], clientId: "foreign-client");
        foreign.ClientSecret = SecretTestValues.Stored("foreign-secret");
        foreign.IssuerUri = "https://identity.other-installation.example.com";
        var sut = _fixture.CreateProvisioningService();

        var report = await sut.EnsureLinkedConfigurationsProvisionedAsync(TenantId);

        using var _ = Assert.Multiple();
        await Assert.That(_fixture.SentClients().Any(c => c.ClientId == "foreign-client")).IsFalse();
        await Assert.That(report.HasChanges).IsFalse();
        await Assert.That(report.HasFailures).IsFalse();
    }

    [Test]
    public async Task EnsureLinkedConfigurations_ReconcilesTheCurrentEntity_NotTheListedSnapshot()
    {
        // Another pass saved a secret after the list was read: the re-read entity wins, so no second
        // secret is issued.
        var listed = _fixture.AddStandalone("accounting-pipeline-reader", ["AccountingRead"]);
        var current = _fixture.AddStandalone("accounting-pipeline-reader", ["AccountingRead"],
            clientId: "octo-pipeline-sa-reader");
        _fixture.Accounts.Remove(current);
        current.RtId = listed.RtId;
        current.ClientSecret = SecretTestValues.Stored("saved-by-the-other-pass");
        _fixture.Repository.GetServiceAccountByRtIdAsync(TenantId, listed.RtId).Returns(current);
        var sut = _fixture.CreateProvisioningService();

        await sut.EnsureLinkedConfigurationsProvisionedAsync(TenantId);

        var sent = _fixture.SentClients().Single(c => c.ClientId == "octo-pipeline-sa-reader");
        await Assert.That(sent.ClientSecret).IsEqualTo("saved-by-the-other-pass");
    }

    [Test]
    public async Task EnsureLinkedConfigurations_AccountDeletedMeanwhile_IsSkipped()
    {
        var gone = _fixture.AddStandalone("accounting-ai-prompt", ["AccountingRead"]);
        _fixture.Repository.GetServiceAccountByRtIdAsync(TenantId, gone.RtId)
            .Returns((RtServiceAccountConfiguration?)null);
        var sut = _fixture.CreateProvisioningService();

        var report = await sut.EnsureLinkedConfigurationsProvisionedAsync(TenantId);

        using var _ = Assert.Multiple();
        await Assert.That(report.HasFailures).IsFalse();
        await Assert.That(_fixture.SentClients().Count).IsEqualTo(0);
    }
}
