using Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Adapters;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Methods;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Methods;

/// <summary>AB#6879: the catalog finds the pipeline-handled methods in the CK caches of the served tenants.</summary>
internal class PipelineMethodCatalogTests
{
    private readonly ICkCacheService _ckCache = Substitute.For<ICkCacheService>();
    private readonly IAdapterCache _adapterCache = Substitute.For<IAdapterCache>();
    private readonly PipelineMethodCatalog _catalog;
    private readonly CkTypeGraph _device;
    private readonly string _updateFirmware;
    private readonly string _reboot;

    public PipelineMethodCatalogTests()
    {
        _catalog = new PipelineMethodCatalog(_ckCache, _adapterCache);
        _device = new CkTypeGraph(new CkId<CkTypeId>("Acme.Assets-1.0.0/Device"), new CkCompiledTypeDto
        {
            Methods =
            [
                Method("UpdateFirmware-1", CkMethodHandlers.Pipeline, 20),
                Method("Reboot-1", null, null),
                Method("Reset-1", CkMethodHandlers.Service, 10)
            ]
        });
        _updateFirmware = _device.AllMethods["UpdateFirmware-1"].QualifiedMethodId;
        _reboot = _device.AllMethods["Reboot-1"].QualifiedMethodId;

        _adapterCache.GetEnabledTenantIds().Returns(["t1", "t2", "unloaded"]);
        _adapterCache.TryGetTenant("t1", out Arg.Any<AdapterTenant?>()).Returns(true);
        _ckCache.IsTenantLoaded("t1").Returns(true);
        _ckCache.IsTenantLoaded("t2").Returns(true);
        _ckCache.IsTenantLoaded("unloaded").Returns(false);
        _ckCache.GetCkTypes("t1").Returns([_device]);
        _ckCache.GetCkTypes("t2").Returns([]);
        _ckCache.TryGetRtCkType("t1", Arg.Any<RtCkId<CkTypeId>>(), out Arg.Any<CkTypeGraph?>())
            .Returns(call =>
            {
                call[2] = _device;
                return true;
            });
    }

    private static CkMethodDto Method(string id, string? handler, int? timeout) => new()
    {
        MethodId = id,
        Execution = handler is null && timeout is null
            ? null
            : new CkMethodExecutionDto { Handler = handler, TimeoutSeconds = timeout }
    };

    [Test]
    public async Task OnlyMethodsDeclaredAsPipelineHandledAreListed()
    {
        var methods = _catalog.GetPipelineMethods();

        await Assert.That(methods.Count).IsEqualTo(1);
        await Assert.That(methods[0].MethodId).IsEqualTo(_updateFirmware);
        await Assert.That(methods[0].TenantId).IsEqualTo("t1");
        await Assert.That(methods[0].PipelineWellKnownName).IsEqualTo(_updateFirmware);
        await Assert.That(methods[0].TimeoutSeconds).IsEqualTo(20);
    }

    [Test]
    public async Task ATenantWhoseCacheIsNotLoadedIsSkipped()
    {
        _ckCache.ClearReceivedCalls();

        _catalog.GetPipelineMethods();

        _ckCache.DidNotReceive().GetCkTypes("unloaded");
        await Task.CompletedTask;
    }

    [Test]
    public async Task FindReturnsTheDefinitionWhateverTheHandlerKind()
    {
        var pipeline = _catalog.Find("t1", _updateFirmware);
        var implicitService = _catalog.Find("t1", _reboot);

        await Assert.That(pipeline!.IsPipelineHandled).IsTrue();
        await Assert.That(implicitService!.IsPipelineHandled).IsFalse();
        await Assert.That(_catalog.Find("t1", "Acme.Assets/Device.Nope-1")).IsNull();
        await Assert.That(_catalog.Find("t1", "garbage")).IsNull();
        await Assert.That(_catalog.Find("unloaded", _updateFirmware)).IsNull();
    }

    [Test]
    public async Task ATenantIsServedWhenItIsCommunicationEnabled()
    {
        await Assert.That(_catalog.IsTenantEnabled("t1")).IsTrue();
        await Assert.That(_catalog.IsTenantEnabled("t2")).IsFalse();
    }
}
