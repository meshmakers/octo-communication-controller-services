using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.SecretDefaultProvisioningServiceTests;

/// <summary>
///     Shared arrangement: an Application in the source tenant P, the subtree C1, C2 and G1 below it, and a
///     repository that records every SecretDefault write per target tenant.
/// </summary>
internal abstract class SecretDefaultProvisioningServiceTestsBase
{
    protected const string SourceTenantId = "parent-p";
    protected const string FirstChildTenantId = "child-c1";
    protected const string SecondChildTenantId = "child-c2";
    protected const string GrandchildTenantId = "grandchild-g1";
    protected const string SiblingTenantId = "sibling-s1";
    protected const string CaptchaKey = "ec-captcha-secret";
    protected const string SentinelSecret = "zq7244-default-value-sentinel";

    protected readonly ICommunicationRepository CommunicationRepository = Substitute.For<ICommunicationRepository>();

    protected readonly ITenantLendingScopeResolver TenantScopeResolver =
        Substitute.For<ITenantLendingScopeResolver>();

    protected readonly ICommunicationEventService CommunicationEventService =
        Substitute.For<ICommunicationEventService>();

    protected readonly TestSecretEncryption SecretEncryption = TestSecretEncryption.Create();

    protected readonly FixedTimeProvider Clock = new(new DateTime(2026, 11, 6, 9, 0, 0, DateTimeKind.Utc));

    protected readonly SecretDefaultProvisioningService Service;

    protected readonly List<RecordedSecretDefaultWrite> Writes = [];

    private readonly Dictionary<string, SecretDefaultWriteOutcome> _outcomeByTenant =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, Exception> _failureByTenant = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<(string TargetTenantId, string Key), RecordedSecretDefaultWrite> _storedDefaults = [];

    private readonly Dictionary<(string TargetTenantId, string Key), OctoObjectId> _storedRtIds = [];

    private readonly Dictionary<string, List<RtApplication>> _applicationsByTenant =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, Exception> _unreadableSourceTenants = new(StringComparer.OrdinalIgnoreCase);

    private readonly HashSet<string> _sourceTenantsWithoutModel = new(StringComparer.OrdinalIgnoreCase);

    protected readonly List<(string TenantId, string Key)> Deletions = [];

    protected SecretDefaultProvisioningServiceTestsBase()
    {
        Service = new SecretDefaultProvisioningService(CommunicationRepository, TenantScopeResolver,
            SecretEncryption.EncryptionService, CommunicationEventService, Clock);

        ArrangeTenant(SourceTenantId, ancestorTenantIds: [],
            descendantTenantIds: [FirstChildTenantId, SecondChildTenantId, GrandchildTenantId]);
        ArrangeTenant(FirstChildTenantId, ancestorTenantIds: [SourceTenantId],
            descendantTenantIds: [GrandchildTenantId]);
        ArrangeTenant(SecondChildTenantId, ancestorTenantIds: [SourceTenantId], descendantTenantIds: []);
        ArrangeTenant(GrandchildTenantId, ancestorTenantIds: [FirstChildTenantId, SourceTenantId],
            descendantTenantIds: []);
        ArrangeTenant(SiblingTenantId, ancestorTenantIds: [], descendantTenantIds: []);

        CommunicationRepository
            .UpsertSecretDefaultAsync(Arg.Any<string>(), Arg.Any<SecretDefaultWrite>())
            .Returns(call =>
            {
                var targetTenantId = call.ArgAt<string>(0);
                var write = call.ArgAt<SecretDefaultWrite>(1);
                if (_failureByTenant.TryGetValue(targetTenantId, out var failure))
                {
                    throw failure;
                }

                var outcome = _outcomeByTenant.GetValueOrDefault(targetTenantId, SecretDefaultWriteOutcome.Created);
                if (outcome is not (SecretDefaultWriteOutcome.Created or SecretDefaultWriteOutcome.Updated))
                {
                    return Task.FromResult(new SecretDefaultWriteResult(outcome));
                }

                var storageKey = (targetTenantId, write.Key);
                if (_storedDefaults.TryGetValue(storageKey, out var stored) &&
                    write.ProtectedSourceTenantIds.Contains(stored.SourceTenantId, StringComparer.OrdinalIgnoreCase))
                {
                    return Task.FromResult(new SecretDefaultWriteResult(SecretDefaultWriteOutcome.KeptAncestorSource,
                        stored.SourceTenantId));
                }

                var recorded = new RecordedSecretDefaultWrite(targetTenantId, write.Key,
                    SecretEncryption.Protector.Unprotect(write.Value), write.SourceTenantId,
                    write.SourceWorkloadRtId, write.ProjectedAt);
                Writes.Add(recorded);
                var isUpdate = _storedDefaults.ContainsKey(storageKey);
                _storedDefaults[storageKey] = recorded;
                _storedRtIds.TryAdd(storageKey, OctoObjectId.GenerateNewId());
                return Task.FromResult(new SecretDefaultWriteResult(isUpdate
                    ? SecretDefaultWriteOutcome.Updated
                    : SecretDefaultWriteOutcome.Created));
            });

        CommunicationRepository
            .GetSecretDefaultsAsync(Arg.Any<string>())
            .Returns(call => Task.FromResult(InventoryOf(call.ArgAt<string>(0))));

        CommunicationRepository
            .DeleteSecretDefaultIfUnchangedAsync(Arg.Any<string>(), Arg.Any<StoredSecretDefault>())
            .Returns(call => Task.FromResult(DeleteIfUnchanged(call.ArgAt<string>(0),
                call.ArgAt<StoredSecretDefault>(1))));

        CommunicationRepository
            .GetSecretDefaultSourceApplicationsAsync(Arg.Any<string>())
            .Returns(call =>
            {
                var tenantId = call.ArgAt<string>(0);
                if (_unreadableSourceTenants.TryGetValue(tenantId, out var readFailure))
                {
                    throw readFailure;
                }

                if (_sourceTenantsWithoutModel.Contains(tenantId))
                {
                    return Task.FromResult(SecretDefaultSources.ModelMissing);
                }

                IReadOnlyCollection<RtApplication> applications =
                    _applicationsByTenant.GetValueOrDefault(tenantId)?.ToList() ?? [];
                return Task.FromResult(new SecretDefaultSources(SecretDefaultTenantAvailability.Available,
                    applications));
            });
    }

    /// <summary>Runs between the read of a default and its conditional removal, as a concurrent writer would.</summary>
    protected Action<string, StoredSecretDefault>? BeforeConditionalDelete { get; set; }

    private SecretDefaultDeleteOutcome DeleteIfUnchanged(string tenantId, StoredSecretDefault expectedDefault)
    {
        BeforeConditionalDelete?.Invoke(tenantId, expectedDefault);
        var storedEntry = _storedRtIds.FirstOrDefault(stored =>
            string.Equals(stored.Key.TargetTenantId, tenantId, StringComparison.OrdinalIgnoreCase) &&
            stored.Value == expectedDefault.RtId);
        if (storedEntry.Value == default)
        {
            return SecretDefaultDeleteOutcome.NotFound;
        }

        var currentDefault = _storedDefaults[storedEntry.Key];
        var isUnchanged = currentDefault.Key == expectedDefault.Key &&
                          currentDefault.SourceTenantId == expectedDefault.SourceTenantId &&
                          currentDefault.SourceWorkloadRtId == expectedDefault.SourceWorkloadRtId &&
                          currentDefault.ProjectedAt == expectedDefault.LastProjectedAt;
        if (!isUnchanged)
        {
            return SecretDefaultDeleteOutcome.Changed;
        }

        _storedRtIds.Remove(storedEntry.Key);
        _storedDefaults.Remove(storedEntry.Key);
        Deletions.Add(storedEntry.Key);
        return SecretDefaultDeleteOutcome.Deleted;
    }

    private SecretDefaultInventory InventoryOf(string tenantId)
    {
        var outcome = _outcomeByTenant.GetValueOrDefault(tenantId, SecretDefaultWriteOutcome.Created);
        if (outcome == SecretDefaultWriteOutcome.TenantUnavailable)
        {
            return SecretDefaultInventory.TenantUnavailable;
        }

        if (outcome == SecretDefaultWriteOutcome.ModelMissing)
        {
            return SecretDefaultInventory.ModelMissing;
        }

        var storedDefaults = _storedDefaults
            .Where(stored => string.Equals(stored.Key.TargetTenantId, tenantId, StringComparison.OrdinalIgnoreCase))
            .Select(stored => new StoredSecretDefault(_storedRtIds[stored.Key], stored.Key.Key,
                stored.Value.SourceTenantId, stored.Value.SourceWorkloadRtId)
            {
                LastProjectedAt = stored.Value.ProjectedAt
            })
            .ToList();
        return new SecretDefaultInventory(SecretDefaultTenantAvailability.Available, storedDefaults);
    }

    protected void ArrangeStoredDefault(string targetTenantId, string key, string revealedValue,
        string? sourceTenantId, string? sourceWorkloadRtId)
    {
        var storageKey = (targetTenantId, key);
        _storedDefaults[storageKey] = new RecordedSecretDefaultWrite(targetTenantId, key, revealedValue,
            sourceTenantId, sourceWorkloadRtId, Clock.UtcNow);
        _storedRtIds.TryAdd(storageKey, OctoObjectId.GenerateNewId());
    }

    protected void ArrangeUnreadableSource(string sourceTenantId)
    {
        _unreadableSourceTenants[sourceTenantId] = new InvalidOperationException("source tenant unreadable");
    }

    protected void ArrangeSourceWithoutModel(string sourceTenantId)
    {
        _sourceTenantsWithoutModel.Add(sourceTenantId);
    }

    protected void RemoveApplication(string tenantId, RtApplication application)
    {
        _applicationsByTenant.GetValueOrDefault(tenantId)?.Remove(application);
        CommunicationRepository.GetWorkloadByRtIdAsync(tenantId, application.RtId).Returns((RtDeployableWorkload?)null);
    }

    protected void ArrangeTenant(string tenantId, string[] ancestorTenantIds, string[] descendantTenantIds)
    {
        TenantScopeResolver
            .ResolveLendableTenantsAsync(tenantId,
                Arg.Is<LendingScope>(scope => scope.Mode == LendingScope.Descendants && scope.AllowedTenantIds == null),
                Arg.Any<CancellationToken>())
            .Returns((IReadOnlyCollection<string>)descendantTenantIds.ToList());
        TenantScopeResolver
            .ResolveAncestorTenantsAsync(tenantId, Arg.Any<CancellationToken>())
            .Returns((IReadOnlyCollection<string>)ancestorTenantIds.ToList());
    }

    protected RecordedSecretDefaultWrite? StoredDefault(string targetTenantId, string key)
    {
        return _storedDefaults.GetValueOrDefault((targetTenantId, key));
    }

    protected void ArrangeOutcome(string targetTenantId, SecretDefaultWriteOutcome outcome)
    {
        _outcomeByTenant[targetTenantId] = outcome;
    }

    protected void ArrangeFailure(string targetTenantId, Exception failure)
    {
        _failureByTenant[targetTenantId] = failure;
    }

    protected RtApplication ArrangeApplication(params RtSecretDefaultProjectionRecord[] secretDefaults)
    {
        return ArrangeApplicationIn(SourceTenantId, secretDefaults);
    }

    protected RtApplication ArrangeApplicationIn(string tenantId,
        params RtSecretDefaultProjectionRecord[] secretDefaults)
    {
        var application = RtEntityCreator.CreateApplication("energy-community-app");
        application.DeploymentState = RtDeploymentStateEnum.Deployed;
        application.SecretDefaults = new AttributeRecordValueList<RtSecretDefaultProjectionRecord>();
        foreach (var secretDefault in secretDefaults)
        {
            application.SecretDefaults.Add(secretDefault);
        }

        CommunicationRepository.GetWorkloadByRtIdAsync(tenantId, application.RtId).Returns(application);
        if (!_applicationsByTenant.TryGetValue(tenantId, out var applicationsOfTenant))
        {
            applicationsOfTenant = [];
            _applicationsByTenant[tenantId] = applicationsOfTenant;
        }

        applicationsOfTenant.Add(application);
        return application;
    }

    protected RtSecretDefaultProjectionRecord ProtectedEntry(string key, string plaintext,
        params string[] allowedTenantIds)
    {
        var entry = new RtSecretDefaultProjectionRecord
        {
            Key = key,
            SecretValue = SecretEncryption.Protector.Protect(plaintext)
        };
        if (allowedTenantIds.Length > 0)
        {
            entry.AllowedTenantIds = new AttributeStringValueList();
            foreach (var allowedTenantId in allowedTenantIds)
            {
                entry.AllowedTenantIds.Add(allowedTenantId);
            }
        }

        return entry;
    }

    protected IReadOnlyList<string> TenantsWritten(string key)
    {
        return Writes.Where(write => write.Key == key)
            .Select(write => write.TargetTenantId)
            .OrderBy(tenantId => tenantId, StringComparer.Ordinal)
            .ToList();
    }

    protected IReadOnlyList<string> StoredEventMessages()
    {
        return CommunicationEventService.ReceivedCalls()
            .SelectMany(call => call.GetArguments().OfType<string>())
            .ToList();
    }

    protected static OctoObjectId NewRtId()
    {
        return OctoObjectId.GenerateNewId();
    }

    internal sealed record RecordedSecretDefaultWrite(
        string TargetTenantId,
        string Key,
        string? RevealedValue,
        string? SourceTenantId,
        string? SourceWorkloadRtId,
        DateTime ProjectedAt);
}
