using DotnetEfCoreMcp.Server.Hooks;

namespace DotnetEfCoreMcp.Server.Tests.Hooks;

/// <summary>
/// Covers parsing of the payload dialects the supported clients emit.
/// </summary>
public class HookToolCallTests
{
    [Fact]
    public void TryParse_ReadsClaudeSnakeCasePayload()
    {
        const string payload = """
            {
              "hook_event_name": "PostToolUse",
              "session_id": "abc123",
              "cwd": "C:\\repo",
              "tool_name": "Edit",
              "tool_input": { "file_path": "C:\\repo\\Query.cs", "new_string": "ctx.Orders" }
            }
            """;

        Assert.True(HookToolCall.TryParse(payload, out var call));
        Assert.NotNull(call);
        Assert.Equal("abc123", call.SessionId);
        Assert.Equal("Edit", call.ToolName);
        Assert.Equal("C:\\repo\\Query.cs", call.GetFilePath());
        Assert.Contains("ctx.Orders", call.GetInputText(), StringComparison.Ordinal);
    }

    [Fact]
    public void TryParse_ReadsCopilotCamelCasePayload()
    {
        const string payload = """
            {
              "sessionId": "s-1",
              "cwd": "/repo",
              "toolName": "edit",
              "toolArgs": { "path": "/repo/Query.cs", "content": "db.Orders.ToListAsync()" }
            }
            """;

        Assert.True(HookToolCall.TryParse(payload, out var call));
        Assert.NotNull(call);
        Assert.Equal("s-1", call.SessionId);
        Assert.Equal("edit", call.ToolName);
        Assert.Equal("/repo/Query.cs", call.GetFilePath());
    }

    [Fact]
    public void TryParse_ParsesToolInputSuppliedAsJsonString()
    {
        // Copilot documents tool arguments as "parsed from JSON string when possible", so a client
        // that forwards the unparsed string must still yield usable fields.
        const string payload = """
            {
              "session_id": "s-2",
              "tool_name": "Write",
              "tool_input": "{\"file_path\":\"/repo/A.cs\",\"content\":\"ctx.Orders.Where(o => o.Id > 1)\"}"
            }
            """;

        Assert.True(HookToolCall.TryParse(payload, out var call));
        Assert.NotNull(call);
        Assert.Equal("/repo/A.cs", call.GetFilePath());
        Assert.Contains(".Where(", call.GetInputText(), StringComparison.Ordinal);
    }

    [Fact]
    public void TryParse_DefaultsSessionIdWhenMissing()
    {
        Assert.True(HookToolCall.TryParse("""{"tool_name":"Edit"}""", out var call));
        Assert.NotNull(call);
        Assert.Equal("default", call.SessionId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"session_id":"s"}""")]
    public void TryParse_ReturnsFalseForUnusablePayloads(string? payload)
    {
        Assert.False(HookToolCall.TryParse(payload, out var call));
        Assert.Null(call);
    }

    [Fact]
    public void GetInputText_FlattensNestedStrings()
    {
        const string payload = """
            {
              "session_id": "s",
              "tool_name": "MultiEdit",
              "tool_input": { "edits": [ { "new_string": "ctx.Orders" }, { "new_string": ".Include(o => o.Lines)" } ] }
            }
            """;

        Assert.True(HookToolCall.TryParse(payload, out var call));
        Assert.NotNull(call);

        var text = call.GetInputText();
        Assert.Contains("ctx.Orders", text, StringComparison.Ordinal);
        Assert.Contains(".Include(", text, StringComparison.Ordinal);
    }
}
