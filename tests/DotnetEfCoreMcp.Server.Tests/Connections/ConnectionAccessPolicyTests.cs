using DotnetEfCoreMcp.Server.Connections;
using DotnetEfCoreMcp.Server.DbContextDiscovery;

namespace DotnetEfCoreMcp.Server.Tests.Connections;

/// <summary>Exercises the P0 #9 policy evaluator (<see cref="ConnectionAccessPolicy"/>) in
/// isolation: allow-over-deny precedence, fail-closed default denial, context reachability via a
/// narrower entity-level allow, and <see cref="ConnectionAccessPolicy.EnsureResolvable"/> selector
/// resolution against a discovered model.</summary>
public sealed class ConnectionAccessPolicyTests
{
    private sealed class SampleContext : Microsoft.EntityFrameworkCore.DbContext
    {
        public Microsoft.EntityFrameworkCore.DbSet<Customer> Customers => Set<Customer>();
        public Microsoft.EntityFrameworkCore.DbSet<Order> Orders => Set<Order>();
    }

    private sealed class OtherContext : Microsoft.EntityFrameworkCore.DbContext
    {
        public Microsoft.EntityFrameworkCore.DbSet<Product> Products => Set<Product>();
    }

    private sealed class Customer
    {
        public int Id { get; set; }
    }

    private sealed class Order
    {
        public int Id { get; set; }
    }

    private sealed class Product
    {
        public int Id { get; set; }
    }

    private const string SampleContextFullName = "DotnetEfCoreMcp.Server.Tests.Connections.ConnectionAccessPolicyTests+SampleContext";
    private const string OtherContextFullName = "DotnetEfCoreMcp.Server.Tests.Connections.ConnectionAccessPolicyTests+OtherContext";

    private static ConnectionAccessPolicy CreatePolicy(
        IReadOnlyList<string>? allowContexts = null,
        IReadOnlyList<string>? denyContexts = null,
        IReadOnlyList<EntitySelector>? allowEntities = null,
        IReadOnlyList<EntitySelector>? denyEntities = null) =>
        new()
        {
            AllowContexts = allowContexts ?? [],
            DenyContexts = denyContexts ?? [],
            AllowEntities = allowEntities ?? [],
            DenyEntities = denyEntities ?? [],
        };

    [Fact]
    public void IsContextReachable_WithBlanketContextAllow_ReturnsTrue()
    {
        var policy = CreatePolicy(allowContexts: [SampleContextFullName]);

        Assert.True(policy.IsContextReachable(SampleContextFullName));
    }

    [Fact]
    public void IsContextReachable_WithOnlyEntityLevelAllow_ReturnsTrueForThatContext()
    {
        var policy = CreatePolicy(allowEntities: [new EntitySelector(SampleContextFullName, "Order")]);

        Assert.True(policy.IsContextReachable(SampleContextFullName));
    }

    [Fact]
    public void IsContextReachable_WithNoMatchingSelector_ReturnsFalse()
    {
        var policy = CreatePolicy(allowContexts: [OtherContextFullName]);

        Assert.False(policy.IsContextReachable(SampleContextFullName));
    }

    [Fact]
    public void IsContextReachable_WithNullContextFullName_ReturnsFalse()
    {
        var policy = CreatePolicy(allowContexts: [SampleContextFullName]);

        Assert.False(policy.IsContextReachable(null));
    }

    [Fact]
    public void IsEntityAllowed_WithBlanketContextAllow_AllowsEveryEntityInThatContext()
    {
        var policy = CreatePolicy(allowContexts: [SampleContextFullName]);

        Assert.True(policy.IsEntityAllowed(SampleContextFullName, "Customer"));
        Assert.True(policy.IsEntityAllowed(SampleContextFullName, "Order"));
    }

    [Fact]
    public void IsEntityAllowed_WithNarrowerEntityAllow_AllowsOnlyThatEntity()
    {
        var policy = CreatePolicy(allowEntities: [new EntitySelector(SampleContextFullName, "Order")]);

        Assert.True(policy.IsEntityAllowed(SampleContextFullName, "Order"));
        Assert.False(policy.IsEntityAllowed(SampleContextFullName, "Customer"));
    }

    [Fact]
    public void IsEntityAllowed_WithNoMatchingSelectorAtAll_DeniesByDefault()
    {
        var policy = CreatePolicy();

        Assert.False(policy.IsEntityAllowed(SampleContextFullName, "Order"));
    }

    [Fact]
    public void IsEntityAllowed_WithNullContextFullName_ReturnsFalse()
    {
        var policy = CreatePolicy(allowContexts: [SampleContextFullName]);

        Assert.False(policy.IsEntityAllowed(null, "Order"));
    }

    [Fact]
    public void IsEntityAllowed_AllowAndDenyBothMatch_AllowTakesPrecedence()
    {
        // Same context+entity selector present in both AllowEntities and DenyEntities: allow must
        // win (allow-over-deny precedence is unconditional, not resolved by specificity/ordering).
        var selector = new EntitySelector(SampleContextFullName, "Order");
        var policy = CreatePolicy(allowEntities: [selector], denyEntities: [selector]);

        Assert.True(policy.IsEntityAllowed(SampleContextFullName, "Order"));
    }

    [Fact]
    public void IsEntityAllowed_ContextBlanketAllowedButEntityAlsoDenied_AllowTakesPrecedence()
    {
        // A blanket context allow plus a narrower, conflicting entity-level deny: allow-over-deny
        // means the blanket allow still wins for that entity.
        var policy = CreatePolicy(
            allowContexts: [SampleContextFullName],
            denyEntities: [new EntitySelector(SampleContextFullName, "Order")]);

        Assert.True(policy.IsEntityAllowed(SampleContextFullName, "Order"));
    }

    [Fact]
    public void IsEntityAllowed_OnlyDenyMatches_DeniesByDefault()
    {
        // A deny-only selector with no corresponding allow: still denied, but via the fail-closed
        // "no allow match" path rather than because of the deny list itself.
        var policy = CreatePolicy(denyEntities: [new EntitySelector(SampleContextFullName, "Order")]);

        Assert.False(policy.IsEntityAllowed(SampleContextFullName, "Order"));
    }

    [Fact]
    public void IsContextAllowed_DistinctFromIsContextReachable_RequiresBlanketAllow()
    {
        var policy = CreatePolicy(allowEntities: [new EntitySelector(SampleContextFullName, "Order")]);

        Assert.True(policy.IsContextReachable(SampleContextFullName));
        Assert.False(policy.IsContextAllowed(SampleContextFullName));
    }

    [Fact]
    public void EnsureResolvable_AllSelectorsResolve_DoesNotThrow()
    {
        var policy = CreatePolicy(
            allowContexts: [SampleContextFullName],
            allowEntities: [new EntitySelector(OtherContextFullName, "Product")]);
        var discovered = new[]
        {
            new DbContextDescriptor(nameof(SampleContext), SampleContextFullName, typeof(SampleContext)),
            new DbContextDescriptor(nameof(OtherContext), OtherContextFullName, typeof(OtherContext)),
        };

        var exception = Record.Exception(() => policy.EnsureResolvable("Test", discovered));

        Assert.Null(exception);
    }

    [Fact]
    public void EnsureResolvable_UnresolvedContextSelector_ThrowsConfigurationException()
    {
        var policy = CreatePolicy(allowContexts: ["Nonexistent.Context"]);
        var discovered = new[]
        {
            new DbContextDescriptor(nameof(SampleContext), SampleContextFullName, typeof(SampleContext)),
        };

        var exception = Assert.Throws<ConnectionRegistryConfigurationException>(
            () => policy.EnsureResolvable("Test", discovered));

        Assert.Contains("Nonexistent.Context", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EnsureResolvable_UnresolvedEntitySelector_ThrowsConfigurationException()
    {
        var policy = CreatePolicy(allowEntities: [new EntitySelector(SampleContextFullName, "DoesNotExist")]);
        var discovered = new[]
        {
            new DbContextDescriptor(nameof(SampleContext), SampleContextFullName, typeof(SampleContext)),
        };

        var exception = Assert.Throws<ConnectionRegistryConfigurationException>(
            () => policy.EnsureResolvable("Test", discovered));

        Assert.Contains("DoesNotExist", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EntitySelector_TryParse_RejectsMalformedSelectors()
    {
        Assert.False(EntitySelector.TryParse(null, out _));
        Assert.False(EntitySelector.TryParse("", out _));
        Assert.False(EntitySelector.TryParse("NoColon", out _));
        Assert.False(EntitySelector.TryParse(":EmptyContext", out _));
        Assert.False(EntitySelector.TryParse("EmptyEntity:", out _));
        Assert.False(EntitySelector.TryParse("Too:Many:Colons", out _));
    }

    [Fact]
    public void EntitySelector_TryParse_AcceptsWellFormedSelector()
    {
        var parsed = EntitySelector.TryParse("MyApp.Context:Order", out var selector);

        Assert.True(parsed);
        Assert.Equal("MyApp.Context", selector.ContextFullName);
        Assert.Equal("Order", selector.EntityName);
    }
}
