using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.SecretDefaultProvisioningServiceTests;

/// <summary>
///     The target reconcile on the real tenant tree resolver: a tree whose walk cannot reach every tenant, or whose
///     registry contradicts the walk, must not look like a tenant without ancestors, or the defaults of those
///     ancestors would be erased.
/// </summary>
internal sealed class ProvisionForTargetIncompleteTreeTests : SecretDefaultProvisioningServiceTestsBase
{
    private readonly ISystemContext _systemContext = Substitute.For<ISystemContext>();
    private readonly Dictionary<string, List<string>> _childrenByTenant = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _recordedParentByTenant = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _unresolvableTenantIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly SecretDefaultProvisioningService _serviceOnTheRealTree;

    public ProvisionForTargetIncompleteTreeTests()
    {
        _serviceOnTheRealTree = new SecretDefaultProvisioningService(CommunicationRepository,
            new TenantLendingScopeResolver(_systemContext), SecretEncryption.EncryptionService,
            CommunicationEventService, Clock);

        AddChild(SourceTenantId, FirstChildTenantId);
        AddChild(SourceTenantId, SecondChildTenantId);
        AddChild(FirstChildTenantId, GrandchildTenantId);

        var systemSession = Substitute.For<IOctoAdminSession>();
        _systemContext.GetAdminSessionAsync().Returns(systemSession);
        _systemContext.GetAllTenantsAsync(systemSession, Arg.Any<int?>(), Arg.Any<int?>())
            .Returns(_ => ResultSetOf([new OctoTenant(SourceTenantId, $"db_{SourceTenantId}", null)]));
        _systemContext.TryFindTenantContextAsync(Arg.Any<string>())
            .Returns(call => ContextOf(call.Arg<string>()));
    }

    private void AddChild(string parentTenantId, string childTenantId)
    {
        if (!_childrenByTenant.TryGetValue(parentTenantId, out var childTenantIds))
        {
            childTenantIds = [];
            _childrenByTenant[parentTenantId] = childTenantIds;
        }

        childTenantIds.Add(childTenantId);
        _recordedParentByTenant[childTenantId] = parentTenantId;
    }

    private ITenantContext? ContextOf(string tenantId)
    {
        if (_unresolvableTenantIds.Contains(tenantId))
        {
            return null;
        }

        var tenantContext = Substitute.For<ITenantContext>();
        tenantContext.TenantId.Returns(tenantId);
        var tenantSession = Substitute.For<IOctoAdminSession>();
        tenantContext.GetAdminSessionAsync().Returns(tenantSession);
        tenantContext.GetDirectChildTenantsAsync(tenantSession).Returns(_ => ResultSetOf(
            _childrenByTenant.GetValueOrDefault(tenantId, [])
                .Select(childTenantId => new OctoTenant(childTenantId, $"db_{childTenantId}",
                    _recordedParentByTenant.GetValueOrDefault(childTenantId)))
                .ToList()));
        return tenantContext;
    }

    private static IResultSet<OctoTenant> ResultSetOf(IReadOnlyList<OctoTenant> tenants)
    {
        var resultSet = Substitute.For<IResultSet<OctoTenant>>();
        resultSet.Items.Returns(tenants);
        return resultSet;
    }

    [Test]
    public async Task AnUnresolvableIntermediateTenant_RemovesNothing()
    {
        var application = ArrangeApplication(ProtectedEntry(CaptchaKey, SentinelSecret));
        ArrangeStoredDefault(GrandchildTenantId, CaptchaKey, SentinelSecret, SourceTenantId,
            application.RtId.ToString());
        _unresolvableTenantIds.Add(FirstChildTenantId);

        var result = await _serviceOnTheRealTree.ProvisionForTargetAsync(GrandchildTenantId);

        using var assertionScope = Assert.Multiple();
        await Assert.That(result.Status).IsEqualTo(SecretDefaultTargetStatus.TreeUnreadable);
        await Assert.That(Deletions).IsEmpty();
        await Assert.That(StoredDefault(GrandchildTenantId, CaptchaKey)).IsNotNull();
    }

    [Test]
    public async Task ARegistryEntryNamingAnotherParent_RemovesNothing()
    {
        var application = ArrangeApplication(ProtectedEntry(CaptchaKey, SentinelSecret));
        ArrangeStoredDefault(GrandchildTenantId, CaptchaKey, SentinelSecret, SourceTenantId,
            application.RtId.ToString());
        _recordedParentByTenant[GrandchildTenantId] = SecondChildTenantId;

        var result = await _serviceOnTheRealTree.ProvisionForTargetAsync(GrandchildTenantId);

        using var assertionScope = Assert.Multiple();
        await Assert.That(result.Status).IsEqualTo(SecretDefaultTargetStatus.TreeUnreadable);
        await Assert.That(Deletions).IsEmpty();
        await Assert.That(StoredDefault(GrandchildTenantId, CaptchaKey)).IsNotNull();
    }

    [Test]
    public async Task AConsistentTree_StillReconciles()
    {
        ArrangeApplication(ProtectedEntry(CaptchaKey, SentinelSecret));

        var result = await _serviceOnTheRealTree.ProvisionForTargetAsync(GrandchildTenantId);

        using var assertionScope = Assert.Multiple();
        await Assert.That(result.Status).IsEqualTo(SecretDefaultTargetStatus.Reconciled);
        await Assert.That(StoredDefault(GrandchildTenantId, CaptchaKey)!.SourceTenantId).IsEqualTo(SourceTenantId);
    }
}
