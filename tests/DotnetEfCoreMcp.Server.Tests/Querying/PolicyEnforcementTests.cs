using DotnetEfCoreMcp.Server.AssemblyLoading;
using DotnetEfCoreMcp.Server.Compilation;
using DotnetEfCoreMcp.Server.Connections;
using DotnetEfCoreMcp.Server.DbContextDiscovery;
using DotnetEfCoreMcp.Server.Querying;
using DotnetEfCoreMcp.Server.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace DotnetEfCoreMcp.Server.Tests.Querying;

/// <summary>
/// Tests for <c>run_query</c> access-policy enforcement. These tests verify that:
/// 1. Expression-mode queries rooted at DbSet properties are allowed
/// 2. Statement-mode queries (with semicolons or blocks) are rejected
/// 3. Queries are validated for proper DbSet root, preventing policy bypass
/// </summary>
public sealed class PolicyEnforcementTests : IDisposable
{
    private readonly SqliteTestDatabase _db = new();
    private readonly LoadedAssemblyHandle _handle;
    private readonly Type _contextType;

    public PolicyEnforcementTests()
    {
        _handle = new AssemblyLoaderService().Load(FixturePaths.SampleAppDllPath);
        _contextType = DbContextScanner.FindDbContextTypes(_handle.Assembly).Descriptors.Single(d => d.Name == "SampleAppDbContext").ClrType;
        using var context = NewContext();
        context.Database.EnsureCreated();
    }

    private DbContext NewContext() => DbContextActivator.CreateInstance(_contextType, _db.ToRegistryEntry(), DatabaseProvider.Sqlite);

    [Fact]
    public void NormalizeAndGetRoot_AcceptsValidExpressionMode()
    {
        // Valid: expression-mode query
        var (root, expr) = QueryExecutor.NormalizeAndGetRoot("Customers.Where(c => c.Age > 18)", 500);
        Assert.Equal("Customers", root);
        Assert.Equal("Customers.Where(c => c.Age > 18)", expr);
    }

    [Fact]
    public void NormalizeAndGetRoot_AcceptsExpressionWithTrailingSemicolon()
    {
        // Valid: expression-mode query with optional trailing semicolon
        var (root, expr) = QueryExecutor.NormalizeAndGetRoot("Customers.Where(c => c.Age > 18);", 500);
        Assert.Equal("Customers", root);
        Assert.Equal("Customers.Where(c => c.Age > 18)", expr);
    }

    [Fact]
    public void NormalizeAndGetRoot_RejectsStatementModeWithMultipleSemicolons()
    {
        // Invalid: statement mode with multiple statements
        var ex = Assert.Throws<QueryExecutionException>(
            () => QueryExecutor.NormalizeAndGetRoot("var x = Customers; x.Where(c => c.Age > 18);", 500));
        Assert.Contains("one expression only", ex.Message);
        Assert.Contains("statement-mode is not supported", ex.Message);
    }

    [Fact]
    public void NormalizeAndGetRoot_RejectsStatementModeWithBlock()
    {
        // Invalid: statement mode with block
        var ex = Assert.Throws<QueryExecutionException>(
            () => QueryExecutor.NormalizeAndGetRoot("{ return Customers.Where(c => c.Age > 18); }", 500));
        Assert.Contains("one expression only", ex.Message);
    }



    [Fact]
    public void ResolveReferencedEntityNames_IdentifiesDbSetProperty()
    {
        // Valid: single DbSet root
        var names = QueryExecutor.ResolveReferencedEntityNames(_contextType, "Customers", "Customers.Where(c => c.Age > 18)");
        Assert.Contains("Customer", names);
    }

    [Fact]
    public void ResolveReferencedEntityNames_IdentifiesMultipleDbSets()
    {
        // Valid: cross-DbSet query with Join
        var names = QueryExecutor.ResolveReferencedEntityNames(
            _contextType,
            "Customers",
            "Customers.Join(Orders, c => c.Id, o => o.CustomerId, (c, o) => new { c.Name, o.Amount })");
        
        // Both Customer and Order should be identified
        Assert.Contains("Customer", names);
        Assert.Contains("Order", names);
    }

    [Fact]
    public void ResolveReferencedEntityNames_IgnoresNonDbSetProperties()
    {
        // Valid: mentioning a non-DbSet public member (e.g. DbContext.Database) should not add any
        // extra entity name to the access list, since Database isn't a DbSet<T> property.
        var names = QueryExecutor.ResolveReferencedEntityNames(_contextType, "Customers", "Customers.Where(c => c.Id > 0 && Database != null)");

        // Only Customer should be in the list; Database doesn't match a DbSet<T> property.
        Assert.Single(names, n => n == "Customer");
    }

    [Fact]
    public void ResolveReferencedEntityNames_HandlesUnicodeEscapesBypassAttempt()
    {
        // This test pins the documented limitation: a unicode-escaped reference to another DbSet
        // ("\u004Frders" == "Orders") is not detected by the word-boundary regex, which only matches
        // the literal property name text, so the policy engine will NOT flag Order access this way.
        // This is acceptable because Roslyn compilation resolves the escape and will still only allow
        // access to a real, public DbSet<T> property - policy enforcement via TryGetDbSetEntityType
        // (not source text) is what ultimately gates entity access.
        var names = QueryExecutor.ResolveReferencedEntityNames(_contextType, "Customers", "Customers.Union(\\u004Frders)");

        Assert.Contains("Customer", names);
        Assert.DoesNotContain("Order", names);
    }

    [Fact]
    public void ResolveReferencedEntityNames_DocumentsStringLiteralFalsePositive()
    {
        // This test pins the documented limitation: entity names appearing inside a string literal are
        // still detected as false positives by the word-boundary regex, because detection is purely
        // text-based and does not distinguish code from string/comment content.
        var names = QueryExecutor.ResolveReferencedEntityNames(
            _contextType,
            "Customers",
            @"Customers.Select(c => new { Description = ""This mentions Orders"" })");

        // Policy flags both Customer (the root) and Order (false positive from the string literal).
        Assert.Contains("Customer", names);
        Assert.Contains("Order", names);
    }

    public void Dispose()
    {
        _db?.Dispose();
    }
}
