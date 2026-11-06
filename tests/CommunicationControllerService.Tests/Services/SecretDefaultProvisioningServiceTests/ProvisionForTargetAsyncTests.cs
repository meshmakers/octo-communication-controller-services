using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.SecretDefaultProvisioningServiceTests;

/// <summary>
///     The target side: a tenant loaded or reconciled after the deploy receives the defaults its own tenant and its
///     ancestors provide, and loses the ones nobody provides any more.
/// </summary>
internal sealed class ProvisionForTargetAsyncTests : SecretDefaultProvisioningServiceTestsBase
{
    private const string NewChildTenantId = "child-new";
    private const string NewGrandchildTenantId = "grandchild-new";
    private const string NewSiblingTenantId = "sibling-new";
    private const string OtherKey = "other-app-secret";
    private const string OtherSentinel = "zq7245-other-app-sentinel";

    [Test]
    public async Task ANewChild_ReceivesTheDefaultOfItsParentsApplication()
    {
        ArrangeTenant(NewChildTenantId, ancestorTenantIds: [SourceTenantId], descendantTenantIds: []);
        var application = ArrangeApplication(ProtectedEntry(CaptchaKey, SentinelSecret));

        var result = await Service.ProvisionForTargetAsync(NewChildTenantId);

        using var assertionScope = Assert.Multiple();
        await Assert.That(result.Status).IsEqualTo(SecretDefaultTargetStatus.Reconciled);
        await Assert.That(result.DefaultsWritten).IsEqualTo(1);
        await Assert.That(StoredDefault(NewChildTenantId, CaptchaKey)!.RevealedValue).IsEqualTo(SentinelSecret);
        await Assert.That(StoredDefault(NewChildTenantId, CaptchaKey)!.SourceTenantId).IsEqualTo(SourceTenantId);
        await Assert.That(StoredDefault(NewChildTenantId, CaptchaKey)!.SourceWorkloadRtId)
            .IsEqualTo(application.RtId.ToString());
    }

    [Test]
    public async Task ANewGrandchild_ReceivesTheDefaultOfItsGrandparentsApplication()
    {
        ArrangeTenant(NewGrandchildTenantId, ancestorTenantIds: [FirstChildTenantId, SourceTenantId],
            descendantTenantIds: []);
        ArrangeApplication(ProtectedEntry(CaptchaKey, SentinelSecret));

        await Service.ProvisionForTargetAsync(NewGrandchildTenantId);

        await Assert.That(StoredDefault(NewGrandchildTenantId, CaptchaKey)!.RevealedValue).IsEqualTo(SentinelSecret);
    }

    [Test]
    public async Task ANewSiblingOfTheSource_ReceivesNothing()
    {
        ArrangeTenant(NewSiblingTenantId, ancestorTenantIds: [], descendantTenantIds: []);
        ArrangeApplication(ProtectedEntry(CaptchaKey, SentinelSecret));

        var result = await Service.ProvisionForTargetAsync(NewSiblingTenantId);

        using var assertionScope = Assert.Multiple();
        await Assert.That(result.DefaultsWritten).IsEqualTo(0);
        await Assert.That(StoredDefault(NewSiblingTenantId, CaptchaKey)).IsNull();
    }

    [Test]
    public async Task TheSourceTenantItself_ReceivesItsOwnApplicationsDefault()
    {
        ArrangeApplication(ProtectedEntry(CaptchaKey, SentinelSecret));

        await Service.ProvisionForTargetAsync(SourceTenantId);

        await Assert.That(StoredDefault(SourceTenantId, CaptchaKey)!.RevealedValue).IsEqualTo(SentinelSecret);
    }

    [Test]
    public async Task AllowedTenantIds_WithoutTheTarget_ProjectNothingIntoIt()
    {
        ArrangeTenant(NewChildTenantId, ancestorTenantIds: [SourceTenantId], descendantTenantIds: []);
        ArrangeApplication(ProtectedEntry(CaptchaKey, SentinelSecret, FirstChildTenantId));

        await Service.ProvisionForTargetAsync(NewChildTenantId);

        await Assert.That(StoredDefault(NewChildTenantId, CaptchaKey)).IsNull();
    }

    [Test]
    public async Task ARemovedEntry_IsDeletedWhileAnotherApplicationsDefaultStays()
    {
        var application = ArrangeApplication(ProtectedEntry(CaptchaKey, SentinelSecret));
        var otherApplication = ArrangeApplication(ProtectedEntry(OtherKey, OtherSentinel));
        await Service.ProvisionForTargetAsync(FirstChildTenantId);
        application.SecretDefaults = new AttributeRecordValueList<RtSecretDefaultProjectionRecord>();

        var result = await Service.ProvisionForTargetAsync(FirstChildTenantId);

        using var assertionScope = Assert.Multiple();
        await Assert.That(result.DefaultsRemoved).IsEqualTo(1);
        await Assert.That(StoredDefault(FirstChildTenantId, CaptchaKey)).IsNull();
        await Assert.That(StoredDefault(FirstChildTenantId, OtherKey)!.SourceWorkloadRtId)
            .IsEqualTo(otherApplication.RtId.ToString());
    }

    [Test]
    public async Task ADeletedApplication_LosesItsDefaultsInTheTarget()
    {
        var application = ArrangeApplication(ProtectedEntry(CaptchaKey, SentinelSecret));
        await Service.ProvisionForTargetAsync(GrandchildTenantId);
        RemoveApplication(SourceTenantId, application);

        var result = await Service.ProvisionForTargetAsync(GrandchildTenantId);

        using var assertionScope = Assert.Multiple();
        await Assert.That(result.DefaultsRemoved).IsEqualTo(1);
        await Assert.That(StoredDefault(GrandchildTenantId, CaptchaKey)).IsNull();
    }

    [Test]
    public async Task ADefaultWhoseSourceIsNoAncestorAnyMore_IsDeleted()
    {
        ArrangeStoredDefault(FirstChildTenantId, CaptchaKey, SentinelSecret, SiblingTenantId,
            OctoObjectId.GenerateNewId().ToString());

        var result = await Service.ProvisionForTargetAsync(FirstChildTenantId);

        using var assertionScope = Assert.Multiple();
        await Assert.That(result.DefaultsRemoved).IsEqualTo(1);
        await Assert.That(StoredDefault(FirstChildTenantId, CaptchaKey)).IsNull();
    }

    [Test]
    public async Task AnUnreadableSource_KeepsItsDefaultsAndANamedWarningIsStored()
    {
        var application = ArrangeApplication(ProtectedEntry(CaptchaKey, SentinelSecret));
        await Service.ProvisionForTargetAsync(FirstChildTenantId);
        ArrangeUnreadableSource(SourceTenantId);

        var result = await Service.ProvisionForTargetAsync(FirstChildTenantId);

        using var assertionScope = Assert.Multiple();
        await Assert.That(result.SourcesUnreadable).IsEqualTo(1);
        await Assert.That(result.DefaultsKept).IsEqualTo(1);
        await Assert.That(result.DefaultsRemoved).IsEqualTo(0);
        await Assert.That(StoredDefault(FirstChildTenantId, CaptchaKey)!.SourceWorkloadRtId)
            .IsEqualTo(application.RtId.ToString());
        await CommunicationEventService.Received(1).StoreWarningEventAsync(FirstChildTenantId,
            Arg.Is<string>(message => message.Contains($"'{SourceTenantId}'")), Arg.Any<RtEntityId?>());
    }

    [Test]
    public async Task AnUnreadableTenantTree_ChangesNothingAndWarns()
    {
        ArrangeStoredDefault(FirstChildTenantId, CaptchaKey, SentinelSecret, SiblingTenantId,
            OctoObjectId.GenerateNewId().ToString());
        TenantScopeResolver.ResolveAncestorTenantsAsync(FirstChildTenantId, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("tenant tree unreadable"));

        var result = await Service.ProvisionForTargetAsync(FirstChildTenantId);

        using var assertionScope = Assert.Multiple();
        await Assert.That(result.Status).IsEqualTo(SecretDefaultTargetStatus.TreeUnreadable);
        await Assert.That(StoredDefault(FirstChildTenantId, CaptchaKey)).IsNotNull();
        await Assert.That(Deletions).IsEmpty();
        await CommunicationEventService.Received(1).StoreWarningEventAsync(FirstChildTenantId,
            Arg.Any<string>(), Arg.Any<RtEntityId?>());
    }

    [Test]
    public async Task AnEditedDefault_IsRestoredToTheProjectedValue()
    {
        var application = ArrangeApplication(ProtectedEntry(CaptchaKey, SentinelSecret));
        ArrangeStoredDefault(FirstChildTenantId, CaptchaKey, "zq7245-edited-by-the-tenant", SourceTenantId,
            application.RtId.ToString());

        await Service.ProvisionForTargetAsync(FirstChildTenantId);

        await Assert.That(StoredDefault(FirstChildTenantId, CaptchaKey)!.RevealedValue).IsEqualTo(SentinelSecret);
    }

    [Test]
    public async Task ADeletedDefault_IsRestoredByTheNextReconcile()
    {
        ArrangeApplication(ProtectedEntry(CaptchaKey, SentinelSecret));
        await Service.ProvisionForTargetAsync(FirstChildTenantId);
        var storedRtId = (await CommunicationRepository.GetSecretDefaultsAsync(FirstChildTenantId)).Defaults
            .Single().RtId;
        await CommunicationRepository.DeleteSecretDefaultAsync(FirstChildTenantId, storedRtId);

        await Service.ProvisionForTargetAsync(FirstChildTenantId);

        await Assert.That(StoredDefault(FirstChildTenantId, CaptchaKey)!.RevealedValue).IsEqualTo(SentinelSecret);
    }

    [Test]
    public async Task TwoAncestorsDeclaringOneKey_TheOneCloserToTheRootWins()
    {
        ArrangeApplicationIn(FirstChildTenantId, ProtectedEntry(CaptchaKey, "zq7245-child-value"));
        ArrangeApplication(ProtectedEntry(CaptchaKey, "zq7245-parent-value"));

        await Service.ProvisionForTargetAsync(GrandchildTenantId);

        using var assertionScope = Assert.Multiple();
        await Assert.That(StoredDefault(GrandchildTenantId, CaptchaKey)!.SourceTenantId).IsEqualTo(SourceTenantId);
        await Assert.That(StoredDefault(GrandchildTenantId, CaptchaKey)!.RevealedValue)
            .IsEqualTo("zq7245-parent-value");
    }

    [Test]
    public async Task TheRootSourceRemovingItsEntry_LetsTheNextAncestorProvideIt()
    {
        var parentApplication = ArrangeApplication(ProtectedEntry(CaptchaKey, "zq7245-parent-value"));
        ArrangeApplicationIn(FirstChildTenantId, ProtectedEntry(CaptchaKey, "zq7245-child-value"));
        await Service.ProvisionForTargetAsync(GrandchildTenantId);
        parentApplication.SecretDefaults = new AttributeRecordValueList<RtSecretDefaultProjectionRecord>();

        await Service.ProvisionForTargetAsync(GrandchildTenantId);

        using var assertionScope = Assert.Multiple();
        await Assert.That(StoredDefault(GrandchildTenantId, CaptchaKey)!.SourceTenantId)
            .IsEqualTo(FirstChildTenantId);
        await Assert.That(StoredDefault(GrandchildTenantId, CaptchaKey)!.RevealedValue)
            .IsEqualTo("zq7245-child-value");
    }

    [Test]
    public async Task AnInvalidEntry_IsNeitherWrittenNorDeleted()
    {
        var application = ArrangeApplication(ProtectedEntry(CaptchaKey, SentinelSecret));
        await Service.ProvisionForTargetAsync(FirstChildTenantId);
        application.SecretDefaults = new AttributeRecordValueList<RtSecretDefaultProjectionRecord>
        {
            ProtectedEntry(CaptchaKey, SentinelSecret),
            ProtectedEntry(CaptchaKey, "zq7245-duplicate")
        };
        var writesBefore = Writes.Count;

        var result = await Service.ProvisionForTargetAsync(FirstChildTenantId);

        using var assertionScope = Assert.Multiple();
        await Assert.That(Writes.Count).IsEqualTo(writesBefore);
        await Assert.That(result.DefaultsRemoved).IsEqualTo(0);
        await Assert.That(StoredDefault(FirstChildTenantId, CaptchaKey)).IsNotNull();
    }

    [Test]
    [Arguments(SecretDefaultWriteOutcome.TenantUnavailable, SecretDefaultTargetStatus.TenantUnavailable)]
    [Arguments(SecretDefaultWriteOutcome.ModelMissing, SecretDefaultTargetStatus.ModelMissing)]
    public async Task ATargetThatCannotHoldDefaults_IsSkipped(SecretDefaultWriteOutcome tenantState,
        SecretDefaultTargetStatus expectedStatus)
    {
        ArrangeApplication(ProtectedEntry(CaptchaKey, SentinelSecret));
        ArrangeOutcome(FirstChildTenantId, tenantState);

        var result = await Service.ProvisionForTargetAsync(FirstChildTenantId);

        using var assertionScope = Assert.Multiple();
        await Assert.That(result.Status).IsEqualTo(expectedStatus);
        await Assert.That(Writes).IsEmpty();
    }

    [Test]
    public async Task AFailedWrite_IsCountedAndReportedWithoutTheValue()
    {
        ArrangeApplication(ProtectedEntry(CaptchaKey, SentinelSecret));
        ArrangeFailure(FirstChildTenantId, new InvalidOperationException("write refused"));

        var result = await Service.ProvisionForTargetAsync(FirstChildTenantId);

        using var assertionScope = Assert.Multiple();
        await Assert.That(result.WritesFailed).IsEqualTo(1);
        await Assert.That(StoredEventMessages().Any(message => message.Contains(CaptchaKey))).IsTrue();
        await Assert.That(StoredEventMessages().Any(message => message.Contains(SentinelSecret))).IsFalse();
    }

    [Test]
    public async Task EventsOfAReconcile_NameTenantsAndKeysButNeverTheValue()
    {
        var application = ArrangeApplication(ProtectedEntry(CaptchaKey, SentinelSecret));
        await Service.ProvisionForTargetAsync(FirstChildTenantId);
        application.SecretDefaults = new AttributeRecordValueList<RtSecretDefaultProjectionRecord>();
        await Service.ProvisionForTargetAsync(FirstChildTenantId);
        ArrangeUnreadableSource(SourceTenantId);
        await Service.ProvisionForTargetAsync(FirstChildTenantId);

        using var assertionScope = Assert.Multiple();
        await Assert.That(StoredEventMessages()).IsNotEmpty();
        await Assert.That(StoredEventMessages().Any(message => message.Contains(SentinelSecret))).IsFalse();
    }

    [Test]
    [NotInParallel(nameof(NLog.LogManager))]
    public async Task TheSentinel_ReachesNoLogLineOfAReconcile()
    {
        var application = ArrangeApplication(ProtectedEntry(CaptchaKey, SentinelSecret));
        ArrangeStoredDefault(FirstChildTenantId, OtherKey, OtherSentinel, SiblingTenantId,
            OctoObjectId.GenerateNewId().ToString());
        ArrangeFailure(SecondChildTenantId, new InvalidOperationException("write refused"));
        var memoryTarget = new NLog.Targets.MemoryTarget("secret-default-target-probe")
        {
            Layout = "${level}|${message}|${exception:format=ToString}|${all-event-properties}"
        };
        var previousConfiguration = NLog.LogManager.Configuration;
        var probeConfiguration = new NLog.Config.LoggingConfiguration();
        probeConfiguration.AddRule(NLog.LogLevel.Trace, NLog.LogLevel.Fatal, memoryTarget);
        NLog.LogManager.Configuration = probeConfiguration;
        try
        {
            await Service.ReconcileTenantAndSubtreeAsync(SourceTenantId);
            application.SecretDefaults = new AttributeRecordValueList<RtSecretDefaultProjectionRecord>();
            await Service.ReconcileTenantAndSubtreeAsync(SourceTenantId);

            using var assertionScope = Assert.Multiple();
            await Assert.That(memoryTarget.Logs).IsNotEmpty();
            await Assert.That(memoryTarget.Logs.Any(line =>
                line.Contains(SentinelSecret[..12], StringComparison.Ordinal) ||
                line.Contains(OtherSentinel[..12], StringComparison.Ordinal))).IsFalse();
        }
        finally
        {
            NLog.LogManager.Configuration = previousConfiguration;
        }
    }

    [Test]
    public async Task ReconcileTenantAndSubtree_ReturnsOneResultPerTenant()
    {
        ArrangeApplication(ProtectedEntry(CaptchaKey, SentinelSecret));

        var results = await Service.ReconcileTenantAndSubtreeAsync(SourceTenantId);

        using var assertionScope = Assert.Multiple();
        await Assert.That(results.Select(result => result.TenantId).First()).IsEqualTo(SourceTenantId);
        await Assert.That(results.Select(result => result.TenantId)).IsEquivalentTo(new[]
            { SourceTenantId, FirstChildTenantId, SecondChildTenantId, GrandchildTenantId });
        await Assert.That(results.All(result => result.DefaultsWritten == 1)).IsTrue();
    }

    [Test]
    public async Task ReconcileTenantAndSubtree_OneThrowingTenant_DoesNotStopTheOthers()
    {
        ArrangeApplication(ProtectedEntry(CaptchaKey, SentinelSecret));
        CommunicationRepository.GetSecretDefaultsAsync(SecondChildTenantId)
            .ThrowsAsync(new InvalidOperationException("tenant database unreachable"));

        var results = await Service.ReconcileTenantAndSubtreeAsync(SourceTenantId);

        using var assertionScope = Assert.Multiple();
        await Assert.That(results.Count).IsEqualTo(4);
        await Assert.That(results.Single(result => result.TenantId == SecondChildTenantId).WritesFailed)
            .IsEqualTo(1);
        await Assert.That(StoredDefault(GrandchildTenantId, CaptchaKey)).IsNotNull();
    }
}
