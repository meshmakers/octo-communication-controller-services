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

        // 🔴 ONLY the roots, because that is all the real system registry holds (AB#5271).
        //
        // A tenant's registry entry lives in its PARENT's database, so `GetAllTenantsAsync` on the
        // system context returns the system tenant's own direct children and nothing deeper. This
        // fake used to return every tenant with its parent filled in, which made the resolver look
        // correct while it was reading a table that, in production, does not contain a grandchild at
        // all — every tenant below the first level resolved to "no parent", so it had no ancestors
        // and no siblings, silently. `AGrandchildResolvesItsAncestorsAndSiblings` is the test that
        // fails if the parent lookup goes back to this call.
        var roots = _parents.Where(kvp => kvp.Value is null)
            .Select(kvp => new OctoTenant(kvp.Key, $"db_{kvp.Key}", null))
            .ToList();
        var adminSession = Substitute.For<IOctoAdminSession>();
        _systemContext.GetAdminSessionAsync().Returns(adminSession);
        _systemContext.GetAllTenantsAsync(adminSession, Arg.Any<int?>(), Arg.Any<int?>())
            .Returns(_ => CreateResultSet(roots));
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

    /// <summary>
    ///     🔴 AB#5271 — the candidate set for a tenant TWO levels down.
    /// </summary>
    /// <remarks>
    ///     This is the shape the feature actually ships into (octosystem → accounting →
    ///     meshmakers), and it is the one the resolver used to get wrong: the parent was looked up
    ///     in <c>GetAllTenantsAsync</c> on the system context, which holds only the system tenant's
    ///     own direct children. A grandchild is not in that table at all, so the lookup missed and
    ///     the tenant resolved to "no parent" — no ancestors, no siblings, no lent pools, and no
    ///     error anywhere. Nothing else in this file fails if the parent lookup regresses to that
    ///     call; this test does.
    /// </remarks>
    [Test]
    public async Task AGrandchildResolvesItsAncestorsAndSiblings()
    {
        GivenTree(
            ("root", null),
            ("accounting", "root"),
            ("meshmakers", "accounting"),
            ("gastroacker", "accounting"));

        var candidates = await _resolver.ResolveCandidateLenderTenantsAsync("meshmakers");

        await Assert.That(candidates).Contains("accounting");
        await Assert.That(candidates).Contains("root");
        await Assert.That(candidates).Contains("gastroacker");
        await Assert.That(candidates).DoesNotContain("meshmakers");
    }

    /// <summary>
    ///     The same depth, from the lending side: the ancestor subtraction that keeps a registry
    ///     cycle from turning into an upward escalation needs a real ancestor chain, and at depth
    ///     two it used to have none — so the guard was in the code and not in force.
    /// </summary>
    [Test]
    public async Task AGrandchildsAncestorChainIsAvailableToTheUpwardGuard()
    {
        GivenTree(
            ("root", null),
            ("accounting", "root"),
            ("meshmakers", "accounting"));

        // The corrupted edge: meshmakers lists its own grandparent as a child.
        _children["meshmakers"].Add("root");

        var lendable = await _resolver.ResolveLendableTenantsAsync("meshmakers",
            new LendingScope(LendingScope.Descendants, null));

        await Assert.That(lendable).DoesNotContain("root");
        await Assert.That(lendable).DoesNotContain("accounting");
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
