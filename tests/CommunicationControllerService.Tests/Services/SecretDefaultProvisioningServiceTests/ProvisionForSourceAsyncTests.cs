using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.SecretDefaultProvisioningServiceTests;

/// <summary>
///     The projection of an Application's SecretDefaults into its own tenant and its whole subtree.
/// </summary>
internal sealed class ProvisionForSourceAsyncTests : SecretDefaultProvisioningServiceTestsBase
{
    [Test]
    public async Task OwnTenantAndWholeSubtree_EachReceiveTheDefault()
    {
        var application = ArrangeApplication(ProtectedEntry(CaptchaKey, SentinelSecret));

        var result = await Service.ProvisionForSourceAsync(SourceTenantId, application.RtId);

        using var assertionScope = Assert.Multiple();
        await Assert.That(TenantsWritten(CaptchaKey)).IsEquivalentTo(new[]
            { FirstChildTenantId, SecondChildTenantId, GrandchildTenantId, SourceTenantId });
        await Assert.That(TenantsWritten(CaptchaKey)).DoesNotContain(SiblingTenantId);
        await Assert.That(result.DefaultsWritten).IsEqualTo(4);
        await Assert.That(result.TargetsSkipped).IsEqualTo(0);
        await Assert.That(result.TargetsFailed).IsEqualTo(0);
    }

    [Test]
    public async Task TheSubtreeIsAskedForDescendantsOnly_NeverForSiblings()
    {
        var application = ArrangeApplication(ProtectedEntry(CaptchaKey, SentinelSecret));

        await Service.ProvisionForSourceAsync(SourceTenantId, application.RtId);

        await TenantScopeResolver.DidNotReceive().ResolveLendableTenantsAsync(Arg.Any<string>(),
            Arg.Is<LendingScope>(scope => scope.Mode != LendingScope.Descendants), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task EveryWrite_CarriesTheRevealedValueAndItsSource()
    {
        var application = ArrangeApplication(ProtectedEntry(CaptchaKey, SentinelSecret));

        await Service.ProvisionForSourceAsync(SourceTenantId, application.RtId);

        foreach (var write in Writes)
        {
            using var assertionScope = Assert.Multiple();
            await Assert.That(write.RevealedValue).IsEqualTo(SentinelSecret);
            await Assert.That(write.SourceTenantId).IsEqualTo(SourceTenantId);
            await Assert.That(write.SourceWorkloadRtId).IsEqualTo(application.RtId.ToString());
            await Assert.That(write.ProjectedAt).IsEqualTo(Clock.UtcNow);
        }
    }

    [Test]
    public async Task AllowedTenantIds_NarrowTheTargetsToTheListedSubtreeTenants()
    {
        var application = ArrangeApplication(ProtectedEntry(CaptchaKey, SentinelSecret, FirstChildTenantId));

        await Service.ProvisionForSourceAsync(SourceTenantId, application.RtId);

        await Assert.That(TenantsWritten(CaptchaKey)).IsEquivalentTo(new[] { FirstChildTenantId });
    }

    [Test]
    public async Task AllowedTenantIds_NamingATenantOutsideTheSubtree_NeverWidenTheTargets()
    {
        var application = ArrangeApplication(
            ProtectedEntry(CaptchaKey, SentinelSecret, FirstChildTenantId, SiblingTenantId, "unrelated-root"));

        await Service.ProvisionForSourceAsync(SourceTenantId, application.RtId);

        await Assert.That(TenantsWritten(CaptchaKey)).IsEquivalentTo(new[] { FirstChildTenantId });
    }

    [Test]
    public async Task AllowedTenantIds_MayNameTheSourceTenantItself()
    {
        var application = ArrangeApplication(ProtectedEntry(CaptchaKey, SentinelSecret, SourceTenantId));

        await Service.ProvisionForSourceAsync(SourceTenantId, application.RtId);

        await Assert.That(TenantsWritten(CaptchaKey)).IsEquivalentTo(new[] { SourceTenantId });
    }

    [Test]
    public async Task ARedeployWithAChangedValue_WritesTheNewValueToEveryTarget()
    {
        var application = ArrangeApplication(ProtectedEntry(CaptchaKey, "sentinel-7244-first-value"));
        await Service.ProvisionForSourceAsync(SourceTenantId, application.RtId);

        application.SecretDefaults = new AttributeRecordValueList<RtSecretDefaultProjectionRecord>
            { ProtectedEntry(CaptchaKey, "sentinel-7244-rotated-value") };
        Writes.Clear();
        await Service.ProvisionForSourceAsync(SourceTenantId, application.RtId);

        using var assertionScope = Assert.Multiple();
        await Assert.That(Writes.Count).IsEqualTo(4);
        await Assert.That(Writes.All(write => write.RevealedValue == "sentinel-7244-rotated-value")).IsTrue();
    }

    [Test]
    public async Task TwoDefaults_AreEachWrittenUnderTheirOwnKey()
    {
        var application = ArrangeApplication(
            ProtectedEntry(CaptchaKey, SentinelSecret),
            ProtectedEntry("ec-maps-key", "sentinel-7244-second-value", GrandchildTenantId));

        await Service.ProvisionForSourceAsync(SourceTenantId, application.RtId);

        using var assertionScope = Assert.Multiple();
        await Assert.That(TenantsWritten(CaptchaKey).Count).IsEqualTo(4);
        await Assert.That(TenantsWritten("ec-maps-key")).IsEquivalentTo(new[] { GrandchildTenantId });
    }

    [Test]
    public async Task ATargetWithoutTheModel_IsSkippedWithAWarningNamingTenantAndKey()
    {
        ArrangeOutcome(SecondChildTenantId, SecretDefaultWriteOutcome.ModelMissing);
        var application = ArrangeApplication(ProtectedEntry(CaptchaKey, SentinelSecret));

        var result = await Service.ProvisionForSourceAsync(SourceTenantId, application.RtId);

        using var assertionScope = Assert.Multiple();
        await Assert.That(TenantsWritten(CaptchaKey)).DoesNotContain(SecondChildTenantId);
        await Assert.That(TenantsWritten(CaptchaKey).Count).IsEqualTo(3);
        await Assert.That(result.TargetsSkipped).IsEqualTo(1);
        await CommunicationEventService.Received(1).StoreWarningEventAsync(SourceTenantId,
            Arg.Is<string>(message => message.Contains(SecondChildTenantId) && message.Contains(CaptchaKey)),
            Arg.Any<RtEntityId?>());
    }

    [Test]
    public async Task AnUnreadableTarget_IsSkippedWithAWarningNamingTenantAndKey()
    {
        ArrangeOutcome(GrandchildTenantId, SecretDefaultWriteOutcome.TenantUnavailable);
        var application = ArrangeApplication(ProtectedEntry(CaptchaKey, SentinelSecret));

        var result = await Service.ProvisionForSourceAsync(SourceTenantId, application.RtId);

        using var assertionScope = Assert.Multiple();
        await Assert.That(TenantsWritten(CaptchaKey).Count).IsEqualTo(3);
        await Assert.That(result.TargetsSkipped).IsEqualTo(1);
        await CommunicationEventService.Received(1).StoreWarningEventAsync(SourceTenantId,
            Arg.Is<string>(message => message.Contains(GrandchildTenantId) && message.Contains(CaptchaKey)),
            Arg.Any<RtEntityId?>());
    }

    [Test]
    public async Task AFailingTarget_DoesNotStopTheOthersAndIsReportedAsAnError()
    {
        ArrangeFailure(FirstChildTenantId, new InvalidOperationException("write refused"));
        var application = ArrangeApplication(ProtectedEntry(CaptchaKey, SentinelSecret));

        var result = await Service.ProvisionForSourceAsync(SourceTenantId, application.RtId);

        using var assertionScope = Assert.Multiple();
        await Assert.That(TenantsWritten(CaptchaKey).Count).IsEqualTo(3);
        await Assert.That(result.TargetsFailed).IsEqualTo(1);
        await CommunicationEventService.Received(1).StoreErrorEventAsync(SourceTenantId,
            Arg.Is<string>(message => message.Contains(FirstChildTenantId) && message.Contains(CaptchaKey)),
            Arg.Any<RtEntityId?>());
    }

    [Test]
    public async Task AnUnrevealableValue_WritesNothingForThatKeyAndIsReportedAsAnError()
    {
        var unreadable = new RtSecretDefaultProjectionRecord
        {
            Key = CaptchaKey,
            SecretValue = TestSecretEncryption.Create(keyId: "foreign-key").Protector.Protect(SentinelSecret)
        };
        var application = ArrangeApplication(unreadable);

        var result = await Service.ProvisionForSourceAsync(SourceTenantId, application.RtId);

        using var assertionScope = Assert.Multiple();
        await Assert.That(Writes).IsEmpty();
        await Assert.That(result.DefaultsWritten).IsEqualTo(0);
        await CommunicationEventService.Received(1).StoreErrorEventAsync(SourceTenantId,
            Arg.Is<string>(message => message.Contains(CaptchaKey)), Arg.Any<RtEntityId?>());
    }

    [Test]
    public async Task AnApplicationWithoutSecretDefaults_WritesNothing()
    {
        var application = ArrangeApplication();
        application.SecretDefaults = null;

        var result = await Service.ProvisionForSourceAsync(SourceTenantId, application.RtId);

        using var assertionScope = Assert.Multiple();
        await Assert.That(result).IsEqualTo(SecretDefaultProvisioningResult.Nothing);
        await Assert.That(Writes).IsEmpty();
    }

    [Test]
    public async Task AWorkloadThatIsNotAnApplication_WritesNothing()
    {
        var adapter = RtEntityCreator.CreateAdapter();
        CommunicationRepository.GetWorkloadByRtIdAsync(SourceTenantId, adapter.RtId).Returns(adapter);

        var result = await Service.ProvisionForSourceAsync(SourceTenantId, adapter.RtId);

        await Assert.That(result).IsEqualTo(SecretDefaultProvisioningResult.Nothing);
    }

    [Test]
    public async Task AnUnknownWorkload_WritesNothing()
    {
        var result = await Service.ProvisionForSourceAsync(SourceTenantId, NewRtId());

        await Assert.That(result).IsEqualTo(SecretDefaultProvisioningResult.Nothing);
    }

    [Test]
    public async Task OnlySecretDefaultsAreWritten_NoOtherRepositoryWriteHappens()
    {
        var application = ArrangeApplication(ProtectedEntry(CaptchaKey, SentinelSecret));

        await Service.ProvisionForSourceAsync(SourceTenantId, application.RtId);

        var repositoryCallNames = CommunicationRepository.ReceivedCalls()
            .Select(call => call.GetMethodInfo().Name)
            .Distinct()
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();
        await Assert.That(repositoryCallNames).IsEquivalentTo(new[]
        {
            nameof(ICommunicationRepository.GetSecretDefaultsAsync),
            nameof(ICommunicationRepository.GetWorkloadByRtIdAsync),
            nameof(ICommunicationRepository.UpsertSecretDefaultAsync)
        });
    }

    [Test]
    [NotInParallel(nameof(NLog.LogManager))]
    public async Task ThePlaintext_ReachesNoLogLineAndNoEvent()
    {
        ArrangeOutcome(SecondChildTenantId, SecretDefaultWriteOutcome.ModelMissing);
        ArrangeFailure(FirstChildTenantId, new InvalidOperationException("write refused"));
        var application = ArrangeApplication(ProtectedEntry(CaptchaKey, SentinelSecret));

        var memoryTarget = new NLog.Targets.MemoryTarget("secret-default-projection-probe")
        {
            Layout = "${level}|${message}|${exception:format=ToString}|${all-event-properties}"
        };
        var previousConfiguration = NLog.LogManager.Configuration;
        var probeConfiguration = new NLog.Config.LoggingConfiguration();
        probeConfiguration.AddRule(NLog.LogLevel.Trace, NLog.LogLevel.Fatal, memoryTarget);
        NLog.LogManager.Configuration = probeConfiguration;
        try
        {
            await Service.ProvisionForSourceAsync(SourceTenantId, application.RtId);

            using var assertionScope = Assert.Multiple();
            await Assert.That(Writes.Any(write => write.RevealedValue == SentinelSecret)).IsTrue();
            await Assert.That(memoryTarget.Logs).IsNotEmpty();
            await Assert.That(memoryTarget.Logs.Any(line => line.Contains(SentinelSecret[..12], StringComparison.Ordinal)))
                .IsFalse();
            await Assert.That(StoredEventMessages().Any(message => message.Contains(SentinelSecret[..12],
                StringComparison.Ordinal))).IsFalse();
        }
        finally
        {
            NLog.LogManager.Configuration = previousConfiguration;
        }
    }

    [Test]
    public async Task TheWriteRequest_DoesNotRenderThePlaintext()
    {
        var write = new SecretDefaultWrite(CaptchaKey, RtSecretValue.Pending(SentinelSecret), SourceTenantId,
            NewRtId().ToString(), Clock.UtcNow);

        await Assert.That(write.ToString()).DoesNotContain(SentinelSecret[..12]);
    }

    [Test]
    public async Task TheProjectionSummary_IsLinkedToTheApplication()
    {
        var application = ArrangeApplication(ProtectedEntry(CaptchaKey, SentinelSecret));

        await Service.ProvisionForSourceAsync(SourceTenantId, application.RtId);

        await CommunicationEventService.Received(1).StoreInformationEventAsync(SourceTenantId,
            Arg.Is<string>(message => message.Contains("projected")),
            Arg.Is<RtEntityId?>(link => link != null && link.Value.RtId == application.RtId &&
                                        link.Value.CkTypeId == SystemCommunicationCkIds.RtCkApplicationTypeId));
    }
}
