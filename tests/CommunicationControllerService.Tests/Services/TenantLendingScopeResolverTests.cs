using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services;

/// <summary>
///     Pins the AB#4924 lending scope: a pool lends downwards to its owner's descendants and
///     laterally to siblings under a shared parent, and <b>never upwards</b>.
/// </summary>
/// <remarks>
///     🔴 The test that matters most is <see cref="ARegistryCycleCannotMakeAnAncestorBorrowable" />.
///     The breadth-first walk is copied from <c>GetDescendants</c>, whose <c>visited</c> set stops an
///     endless walk but does <b>not</b> stop an ancestor from appearing in the result — that is
///     acceptable for a listing endpoint and is a privilege escalation here, because this set decides
///     whether one tenant may execute work inside another. Deleting the ancestor subtraction in the
///     resolver makes that one test fail and leaves every other test in this file green.
/// </remarks>
internal class TenantLendingScopeResolverTests
{
    private const string Lender = "parentTenant";

    private readonly ISystemContext _systemContext = Substitute.For<ISystemContext>();
    private readonly TenantLendingScopeResolver _resolver;

    /// <summary>Tenant id -> its direct children, as the registry would report them.</summary>
    private readonly Dictionary<string, List<string>> _children = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Tenant id -> its parent, the independent upward view.</summary>
    private readonly Dictionary<string, string?> _parents = new(StringComparer.OrdinalIgnoreCase);

    public TenantLendingScopeResolverTests()
    {
        _resolver = new TenantLendingScopeResolver(_systemContext);
    }

    private void GivenTree(params (string Tenant, string? Parent)[] tenants)
    {
        foreach (var (tenant, parent) in tenants)
        {
            _parents[tenant] = parent;
            _children.TryAdd(tenant, []);
            if (parent is not null)
            {
                _children.TryAdd(parent, []);
                _children[parent].Add(tenant);
            }
        }

        foreach (var tenant in _children.Keys.ToList())
        {
            var tenantContext = CreateContext(tenant);
            _systemContext.TryFindTenantContextAsync(tenant).Returns(_ => tenantContext);
        }

        var allTenants = _parents.Select(kvp => new OctoTenant(kvp.Key, $"db_{kvp.Key}", kvp.Value)).ToList();
        var adminSession = Substitute.For<IOctoAdminSession>();
        _systemContext.GetAdminSessionAsync().Returns(adminSession);
        _systemContext.GetAllTenantsAsync(adminSession, Arg.Any<int?>(), Arg.Any<int?>())
            .Returns(_ => CreateResultSet(allTenants));
    }

    private ITenantContext CreateContext(string tenantId)
    {
        var context = Substitute.For<ITenantContext>();
        context.TenantId.Returns(tenantId);

        var session = Substitute.For<IOctoAdminSession>();
        context.GetAdminSessionAsync().Returns(session);
        context.GetDirectChildTenantsAsync(session).Returns(_ =>
            CreateResultSet(_children.TryGetValue(tenantId, out var kids)
                ? kids.Select(k => new OctoTenant(k, $"db_{k}", tenantId)).ToList()
                : []));

        context.TryGetChildTenantContextAsync(Arg.Any<string>())
            .Returns(call => CreateContext(call.Arg<string>()));

        return context;
    }

    private static IResultSet<OctoTenant> CreateResultSet(IReadOnlyList<OctoTenant> items)
    {
        var resultSet = Substitute.For<IResultSet<OctoTenant>>();
        resultSet.Items.Returns(items);
        return resultSet;
    }

    private static LendingScope Descendants(params string[] allowed) =>
        new(LendingScope.Descendants, allowed.Length == 0 ? null : allowed);

    private static LendingScope DescendantsAndSiblings(params string[] allowed) =>
        new(LendingScope.DescendantsAndSiblings, allowed.Length == 0 ? null : allowed);

    [Test]
    public async Task NotSharedLendsToNobody()
    {
        GivenTree((Lender, null), ("childA", Lender));

        var result = await _resolver.ResolveLendableTenantsAsync(Lender,
            new LendingScope(LendingScope.NotShared, null));

        await Assert.That(result).IsEmpty();
    }

    [Test]
    public async Task DescendantsReachesChildrenAndGrandchildren()
    {
        GivenTree((Lender, null), ("childA", Lender), ("childB", Lender), ("grandchildA1", "childA"));

        var result = await _resolver.ResolveLendableTenantsAsync(Lender, Descendants());

        await Assert.That(result).Contains("childA");
        await Assert.That(result).Contains("childB");
        await Assert.That(result).Contains("grandchildA1");
    }

    [Test]
    public async Task ALenderNeverLendsToItself()
    {
        GivenTree((Lender, null), ("childA", Lender));

        var result = await _resolver.ResolveLendableTenantsAsync(Lender, Descendants());

        await Assert.That(result).DoesNotContain(Lender);
    }

    [Test]
    public async Task AChildDoesNotLendUpwardsToItsParent()
    {
        // The direction the whole feature depends on. A descendant running work inside its ancestor
        // would invert the trust relationship the tenant tree establishes.
        GivenTree((Lender, null), ("childA", Lender));

        var result = await _resolver.ResolveLendableTenantsAsync("childA", DescendantsAndSiblings());

        await Assert.That(result).DoesNotContain(Lender);
    }

    [Test]
    public async Task ARegistryCycleCannotMakeAnAncestorBorrowable()
    {
        // 🔴 THE test. The registry is corrupted so that the parent also appears as a child of its
        // own child. The BFS `visited` guard (seeded with the walk's root) stops the walk from
        // looping, but it never rejects the parent — it was not visited. Only the explicit ancestor
        // subtraction keeps this from granting a lease upwards.
        GivenTree((Lender, null), ("childA", Lender));
        _children["childA"].Add(Lender);

        var result = await _resolver.ResolveLendableTenantsAsync("childA", Descendants());

        await Assert.That(result).DoesNotContain(Lender);
    }

    [Test]
    public async Task SiblingsAreReachableOnlyInTheSiblingMode()
    {
        GivenTree((Lender, null), ("childA", Lender), ("childB", Lender));

        var descendantsOnly = await _resolver.ResolveLendableTenantsAsync("childA", Descendants());
        _resolver.Invalidate("childA");
        var withSiblings = await _resolver.ResolveLendableTenantsAsync("childA", DescendantsAndSiblings());

        using var _ = Assert.Multiple();
        await Assert.That(descendantsOnly).DoesNotContain("childB");
        await Assert.That(withSiblings).Contains("childB");
    }

    [Test]
    public async Task ARootTenantHasNoSiblings()
    {
        // Two unparented tenants are not siblings. Treating "every other root" as a sibling set
        // would make every unparented tenant lend to every other one, erasing the boundary the
        // tenant tree exists to draw.
        GivenTree((Lender, null), ("otherRoot", null));

        var result = await _resolver.ResolveLendableTenantsAsync(Lender, DescendantsAndSiblings());

        await Assert.That(result).DoesNotContain("otherRoot");
    }

    [Test]
    public async Task TheAllowListIntersectsAndNeverWidens()
    {
        GivenTree((Lender, null), ("childA", Lender), ("childB", Lender), ("strangerTenant", null));

        var result = await _resolver.ResolveLendableTenantsAsync(Lender,
            Descendants("childA", "strangerTenant"));

        using var _ = Assert.Multiple();
        await Assert.That(result).Contains("childA");
        // Narrowed away even though it is a real descendant.
        await Assert.That(result).DoesNotContain("childB");
        // Listed, but outside the subtree — an allow-list can never grant what the tree does not.
        await Assert.That(result).DoesNotContain("strangerTenant");
    }

    [Test]
    public async Task AnUnresolvableLenderLendsToNobody()
    {
        // "Parent tenant deleted while lending" (concept §6). Deny rather than guess: the borrower
        // must fall back to a blocking reason, never to a wider scope.
        _systemContext.TryFindTenantContextAsync("ghostTenant").Returns((ITenantContext?)null);

        var result = await _resolver.ResolveLendableTenantsAsync("ghostTenant", Descendants());

        await Assert.That(result).IsEmpty();
    }

    [Test]
    public async Task MayLendAnswersMembershipOfTheResolvedSet()
    {
        GivenTree((Lender, null), ("childA", Lender), ("strangerTenant", null));

        using var _ = Assert.Multiple();
        await Assert.That(await _resolver.MayLendAsync(Lender, "childA", Descendants())).IsTrue();
        await Assert.That(await _resolver.MayLendAsync(Lender, "strangerTenant", Descendants())).IsFalse();
    }
}
