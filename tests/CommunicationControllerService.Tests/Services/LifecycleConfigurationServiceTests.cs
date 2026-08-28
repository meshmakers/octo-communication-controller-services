using Meshmakers.Octo.Backend.CommunicationControllerServices;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services;

internal class LifecycleConfigurationServiceTests
{
    private const string TenantId = "meshtest";

    private readonly ISystemContext _systemContext = Substitute.For<ISystemContext>();
    private readonly ITenantContext _tenantContext = Substitute.For<ITenantContext>();
    private readonly IOctoAdminSession _session = Substitute.For<IOctoAdminSession>();
    private readonly LifecycleConfigurationService _service;

    public LifecycleConfigurationServiceTests()
    {
        _systemContext.FindTenantContextAsync(TenantId).Returns(_tenantContext);
        _tenantContext.GetAdminSessionAsync().Returns(_session);
        _service = new LifecycleConfigurationService(
            Substitute.For<ILogger<LifecycleConfigurationService>>(), _systemContext);
    }

    private void GivenStoredConfiguration(CommunicationLifecycleConfiguration? configuration)
    {
        _tenantContext.GetConfigurationAsync<CommunicationLifecycleConfiguration>(
                _session, Constants.CommunicationLifecycleConfigurationKey, null)
            .Returns(configuration);
    }

    [Test]
    public async Task GetConfigurationAsync_NoStoredRecord_ReturnsDefaults()
    {
        GivenStoredConfiguration(null);

        var configuration = await _service.GetConfigurationAsync(TenantId);

        await Assert.That(configuration).IsNotNull();
        await Assert.That(configuration.ScaleToZeroEnabled).IsFalse();
    }

    [Test]
    public async Task GetConfigurationAsync_StoredRecord_IsReturned()
    {
        GivenStoredConfiguration(new CommunicationLifecycleConfiguration { ScaleToZeroEnabled = true });

        var configuration = await _service.GetConfigurationAsync(TenantId);

        await Assert.That(configuration.ScaleToZeroEnabled).IsTrue();
    }

    [Test]
    public async Task IsScaleToZeroEnabledAsync_ReflectsStoredConfiguration()
    {
        GivenStoredConfiguration(new CommunicationLifecycleConfiguration { ScaleToZeroEnabled = true });

        await Assert.That(await _service.IsScaleToZeroEnabledAsync(TenantId)).IsTrue();
    }

    [Test]
    public async Task GetConfigurationAsync_SecondReadWithinTtl_DoesNotHitTheStoreAgain()
    {
        GivenStoredConfiguration(new CommunicationLifecycleConfiguration { ScaleToZeroEnabled = true });

        var first = await _service.GetConfigurationAsync(TenantId);
        var second = await _service.GetConfigurationAsync(TenantId);

        await Assert.That(second.ScaleToZeroEnabled).IsEqualTo(first.ScaleToZeroEnabled);
        await _systemContext.Received(1).FindTenantContextAsync(TenantId);
        await _tenantContext.Received(1).GetConfigurationAsync<CommunicationLifecycleConfiguration>(
            _session, Constants.CommunicationLifecycleConfigurationKey, null);
    }

    [Test]
    public async Task SetConfigurationAsync_PersistsAndInvalidatesTheCache()
    {
        GivenStoredConfiguration(null);
        // Prime the cache with the defaults.
        _ = await _service.GetConfigurationAsync(TenantId);

        var updated = new CommunicationLifecycleConfiguration { ScaleToZeroEnabled = true };
        await _service.SetConfigurationAsync(TenantId, updated);

        await _tenantContext.Received(1).SetConfigurationAsync(
            _session,
            Constants.CommunicationLifecycleConfigurationKey,
            Arg.Is<object>(o => ReferenceEquals(o, updated)));

        // The cache entry must be gone: the next read hits the store again
        // instead of serving the stale pre-write defaults.
        GivenStoredConfiguration(updated);
        var reread = await _service.GetConfigurationAsync(TenantId);

        await Assert.That(reread.ScaleToZeroEnabled).IsTrue();
        await _tenantContext.Received(2).GetConfigurationAsync<CommunicationLifecycleConfiguration>(
            _session, Constants.CommunicationLifecycleConfigurationKey, null);
    }
}
