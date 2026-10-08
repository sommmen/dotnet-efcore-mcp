using System.Text.Json;
using DotnetEfCoreMcp.Server.Hooks;

namespace DotnetEfCoreMcp.Server.Tests.Hooks;

/// <summary>
/// Covers the detection heuristics that decide whether a tool call earns a reminder.
/// </summary>
public class ToolCallClassifierTests
{
    [Theory]
    // An EF query rooted on a context member.
    [InlineData("var orders = await _context.Orders.Where(o => o.Total > 10).ToListAsync();")]
    // Navigation loading is EF-only.
    [InlineData("query = query.Include(o => o.Lines).ThenInclude(l => l.Product);")]
    // Query-comprehension syntax over a DbSet.
    [InlineData("var q = from o in context.Orders where o.Id > 1 select o;")]
    // An explicitly typed IQueryable.
    [InlineData("IQueryable<Order> q = source.Where(o => o.Name.Substring(0, 2) == \"ab\");")]
    [InlineData("await db.Customers.AnyAsync(c => c.Email == email);")]
    public void ContainsEfCoreLinq_DetectsEfQueries(string code) =>
        Assert.True(ToolCallClassifier.ContainsEfCoreLinq(code));

    [Theory]
    // In-memory LINQ over collections is the dominant false positive this must avoid.
    [InlineData("var names = people.Where(p => p.Age > 18).Select(p => p.Name).ToList();")]
    [InlineData("var first = list.FirstOrDefault(x => x.Id == id);")]
    [InlineData("return items.OrderBy(i => i.Name).ToArray();")]
    // No query operator at all.
    [InlineData("var context = new AppDbContext(options);")]
    [InlineData("")]
    [InlineData("   ")]
    public void ContainsEfCoreLinq_IgnoresNonEfCode(string code) =>
        Assert.False(ToolCallClassifier.ContainsEfCoreLinq(code));

    [Fact]
    public void Classify_FlagsLinqWriteForCSharpEdit()
    {
        var call = CreateCall("Edit", new
        {
            file_path = "/repo/src/Orders/OrderQueries.cs",
            new_string = "return await _context.Orders.Where(o => o.Total > 10).ToListAsync();",
        });

        Assert.Equal(ToolCallKind.LinqWrite, ToolCallClassifier.Classify(call));
    }

    [Fact]
    public void Classify_IgnoresLinqWriteInsideMigrations()
    {
        // Generated migration code is never worth validating against the live model.
        var call = CreateCall("Edit", new
        {
            file_path = "/repo/src/Migrations/20240101_Init.cs",
            new_string = "_context.Orders.Where(o => o.Id > 1).ToListAsync();",
        });

        Assert.Equal(ToolCallKind.Unrelated, ToolCallClassifier.Classify(call));
    }

    [Fact]
    public void Classify_IgnoresNonCSharpWrites()
    {
        var call = CreateCall("Write", new
        {
            file_path = "/repo/README.md",
            content = "_context.Orders.Where(o => o.Id > 1).ToListAsync();",
        });

        Assert.Equal(ToolCallKind.Unrelated, ToolCallClassifier.Classify(call));
    }

    [Fact]
    public void Classify_FlagsApplyPatchWithoutFilePath()
    {
        // Codex's apply_patch carries the target path inside the patch body rather than as a field.
        var call = CreateCall("apply_patch", new
        {
            command = "*** Update File: src/Orders.cs\n+ _context.Orders.Where(o => o.Id > 1).ToListAsync();",
        });

        Assert.Equal(ToolCallKind.LinqWrite, ToolCallClassifier.Classify(call));
    }

    [Theory]
    [InlineData("dotnet run --project src/App")]
    [InlineData("dotnet test")]
    [InlineData("dotnet ef migrations add Init")]
    public void Classify_FlagsBuildCommands(string command)
    {
        var call = CreateCall("Bash", new { command });
        Assert.Equal(ToolCallKind.LinqWrite, ToolCallClassifier.Classify(call));
    }

    [Fact]
    public void Classify_IgnoresUnrelatedShellCommands()
    {
        var call = CreateCall("Bash", new { command = "git status" });
        Assert.Equal(ToolCallKind.Unrelated, ToolCallClassifier.Classify(call));
    }

    [Theory]
    [InlineData("mcp__dotnet-efcore__run_query")]
    [InlineData("mcp__efcore__get_schema")]
    [InlineData("run_query")]
    [InlineData("search_schema")]
    public void Classify_RecognizesEfCoreMcpToolUse(string toolName)
    {
        var call = CreateCall(toolName, new { contextName = "AppDbContext" });
        Assert.Equal(ToolCallKind.EfCoreMcpToolUse, ToolCallClassifier.Classify(call));
    }

    [Theory]
    [InlineData("/repo/src/Data/AppDbContext.cs")]
    [InlineData("/repo/src/Entities/Order.cs")]
    [InlineData("C:\\repo\\src\\Models\\Customer.cs")]
    public void Classify_FlagsEntityReads(string filePath)
    {
        var call = CreateCall("Read", new { file_path = filePath });
        Assert.Equal(ToolCallKind.EntityRead, ToolCallClassifier.Classify(call));
    }

    [Fact]
    public void Classify_IgnoresReadsOutsideEntityFolders()
    {
        var call = CreateCall("Read", new { file_path = "/repo/src/Web/Program.cs" });
        Assert.Equal(ToolCallKind.Unrelated, ToolCallClassifier.Classify(call));
    }

    private static HookToolCall CreateCall(string toolName, object toolInput) => new()
    {
        SessionId = "s",
        ToolName = toolName,
        ToolInput = JsonSerializer.SerializeToElement(toolInput),
    };
}
