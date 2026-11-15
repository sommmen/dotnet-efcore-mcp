using System.Text.Json;
using DotnetEfCoreMcp.Server.Hooks;

namespace DotnetEfCoreMcp.Server.Tests.Cli;

public sealed class CliHookTests : IDisposable
{
    private readonly string _stateDirectory =
        Path.Combine(Path.GetTempPath(), "efcore-cli-hook-tests", Guid.NewGuid().ToString("n"));

    [Theory]
    [InlineData("dotnet-efcore-mcp cli run_query --context-name AppDbContext --query \"Orders.Take(1)\"")]
    [InlineData("dotnet-efcore-mcp cli run-query --context-name AppDbContext --query \"Orders.Take(1)\"")]
    [InlineData("dotnet-efcore-mcp cli preview-query-sql --context-name AppDbContext --query \"Orders\"")]
    [InlineData("dotnet-efcore-mcp cli --assembly bin/App.dll get-schema --context-name AppDbContext")]
    [InlineData("dotnet-efcore-mcp cli --connection Dev search_schema --query order")]
    [InlineData("dotnet-efcore-mcp.exe cli list-entities --context-name AppDbContext")]
    [InlineData("dnx DotnetEfCoreMcp.Server -- cli run-query --context-name AppDbContext --query Orders")]
    [InlineData("npx -y dotnet-efcore-mcp cli get-entity-schema --entity-name Order --context-name AppDbContext")]
    [InlineData("dotnet src/DotnetEfCoreMcp.Server/bin/Debug/net10.0/dotnet-efcore-mcp.dll cli run-query --query Orders")]
    [InlineData("cd repo && dotnet-efcore-mcp cli run-query --query Orders | head -20")]
    public void Classify_RecognizesCliQueryCommandsInAShell(string command)
    {
        foreach (var shell in new[] { "Bash", "powershell", "run_in_terminal" })
        {
            Assert.Equal(ToolCallKind.EfCoreCliToolUse, ToolCallClassifier.Classify(CreateCall(shell, new { command })));
        }
    }

    [Theory]
    [InlineData("dotnet-efcore-mcp cli list-contexts")]
    [InlineData("dotnet-efcore-mcp cli load-assembly --assembly-path x.dll")]
    [InlineData("dotnet-efcore-mcp cli --help")]
    [InlineData("dotnet-efcore-mcp hooks status")]
    [InlineData("dotnet-efcore-mcp version")]
    [InlineData("dotnet-efcore-mcp run-query")]
    [InlineData("git status")]
    [InlineData("grep run-query docs/cli.md")]
    [InlineData("dotnet-efcore-mcp cli; run-query")]
    public void Classify_IgnoresShellCommandsThatAreNotCliQueryOperations(string command)
    {
        Assert.NotEqual(ToolCallKind.EfCoreCliToolUse, ToolCallClassifier.Classify(CreateCall("Bash", new { command })));
    }

    [Fact]
    public void Classify_DoesNotTreatACliCommandAsAShellToolInANonShellTool()
    {
        var call = CreateCall("Edit", new { file_path = "/repo/README.md", new_string = "dotnet-efcore-mcp cli run-query" });

        Assert.Equal(ToolCallKind.Unrelated, ToolCallClassifier.Classify(call));
    }

    [Fact]
    public void Evaluate_ACliQueryCommandClearsTheCooldownLikeAnMcpCall()
    {
        var reminder = CreateReminder();
        Assert.Contains("LINQ validation", reminder.Evaluate(LinqWritePayload("c1"), HookClient.Claude), StringComparison.Ordinal);
        Assert.Equal("{}", reminder.Evaluate(LinqWritePayload("c1"), HookClient.Claude));

        Assert.Equal("{}", reminder.Evaluate(Payload("c1", "Bash", new { command = "dotnet-efcore-mcp cli run-query --query Orders" }), HookClient.Claude));

        Assert.Contains("LINQ validation", reminder.Evaluate(LinqWritePayload("c1"), HookClient.Claude), StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_AfterACliCallTheLinqReminderNamesShellCommands()
    {
        var reminder = CreateReminder();
        reminder.Evaluate(Payload("c2", "Bash", new { command = "dotnet-efcore-mcp cli run-query --query Orders" }), HookClient.Claude);

        var response = reminder.Evaluate(LinqWritePayload("c2"), HookClient.Claude);

        var text = ReminderText(response);
        Assert.Contains("dotnet-efcore-mcp cli preview-query-sql", text, StringComparison.Ordinal);
        Assert.Contains("--assembly", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_WithoutACliCallTheLinqReminderStillNamesMcpTools()
    {
        var response = CreateReminder().Evaluate(LinqWritePayload("c3"), HookClient.Claude);

        var text = ReminderText(response);
        Assert.Contains("`preview_query_sql`", text, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet-efcore-mcp cli", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_AMcpCallAfterACliCallSwitchesBackToMcpWording()
    {
        var reminder = CreateReminder();
        reminder.Evaluate(Payload("c4", "Bash", new { command = "dotnet-efcore-mcp cli run-query --query Orders" }), HookClient.Claude);
        reminder.Evaluate(Payload("c4", "mcp__efcore__run_query", new { contextName = "AppDbContext" }), HookClient.Claude);

        var response = reminder.Evaluate(LinqWritePayload("c4"), HookClient.Claude);

        Assert.Contains("`preview_query_sql`", ReminderText(response), StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_AfterACliCallTheEntityReminderNamesShellCommands()
    {
        var reminder = CreateReminder();
        reminder.Evaluate(Payload("c5", "Bash", new { command = "dotnet-efcore-mcp cli get-schema --context-name X" }), HookClient.Claude);
        var read = Payload("c5", "Read", new { file_path = "/repo/src/Entities/Order.cs" });

        string response = "{}";
        for (var i = 0; i < HookReminder.EntityReadThreshold; i++)
        {
            response = reminder.Evaluate(read, HookClient.Claude);
        }

        Assert.Contains("dotnet-efcore-mcp cli search-schema", ReminderText(response), StringComparison.Ordinal);
    }

    private static string ReminderText(string response)
    {
        using var document = JsonDocument.Parse(response);
        return document.RootElement.GetProperty("additionalContext").GetString() ?? string.Empty;
    }

    private HookReminder CreateReminder() => new(new HookSessionStore(_stateDirectory));

    private static HookToolCall CreateCall(string toolName, object toolInput) => new()
    {
        SessionId = "s",
        ToolName = toolName,
        ToolInput = JsonSerializer.SerializeToElement(toolInput),
    };

    private static string LinqWritePayload(string sessionId) => Payload(sessionId, "Edit", new
    {
        file_path = "/repo/src/Orders/OrderQueries.cs",
        new_string = "return await _context.Orders.Where(o => o.Total > 10).ToListAsync();",
    });

    private static string Payload(string sessionId, string toolName, object toolInput) =>
        JsonSerializer.Serialize(new
        {
            hook_event_name = "PostToolUse",
            session_id = sessionId,
            tool_name = toolName,
            tool_input = toolInput,
        });

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_stateDirectory))
            {
                Directory.Delete(_stateDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}
