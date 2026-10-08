using System.Text;
using System.Text.Json;

namespace DotnetEfCoreMcp.Server.Hooks;

/// <summary>
/// A single tool call reported by an agent's <c>PostToolUse</c> hook, normalized across the three
/// supported clients.
/// </summary>
/// <remarks>
/// The clients disagree on casing and nesting even though they share an event vocabulary:
/// Claude Code and Codex send <c>tool_name</c>/<c>tool_input</c>/<c>session_id</c>, while Copilot
/// CLI sends <c>toolName</c>/<c>toolArgs</c>/<c>sessionId</c> for natively-named (camelCase) events
/// and the snake_case spelling for the PascalCase "VS Code compatible" events this server installs.
/// Parsing accepts either spelling for every field so a config installed in one dialect still works
/// if the client later reports the other.
/// </remarks>
public sealed record HookToolCall
{
    /// <summary>The agent session this call belongs to; used to scope reminder state.</summary>
    public required string SessionId { get; init; }

    /// <summary>The tool name as reported by the client, e.g. <c>Edit</c>, <c>bash</c>, <c>apply_patch</c>.</summary>
    public required string ToolName { get; init; }

    /// <summary>The raw tool arguments, or <see langword="null"/> when the client sent none.</summary>
    public JsonElement? ToolInput { get; init; }

    /// <summary>The client's working directory, when reported.</summary>
    public string? Cwd { get; init; }

    /// <summary>
    /// Parses a hook payload. Returns <see langword="false"/> for malformed or non-object input
    /// rather than throwing: a reminder hook must never disrupt the session that invoked it.
    /// </summary>
    public static bool TryParse(string? payload, out HookToolCall? call)
    {
        call = null;

        if (string.IsNullOrWhiteSpace(payload))
        {
            return false;
        }

        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(payload);
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return false;
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var toolName = ReadString(root, "tool_name", "toolName");
        if (string.IsNullOrWhiteSpace(toolName))
        {
            return false;
        }

        // Codex and Claude always send a session id, but a client that omits it still gets a
        // working (if unscoped) reminder rather than a hard failure.
        var sessionId = ReadString(root, "session_id", "sessionId");

        call = new HookToolCall
        {
            SessionId = string.IsNullOrWhiteSpace(sessionId) ? "default" : sessionId,
            ToolName = toolName,
            ToolInput = ReadProperty(root, "tool_input", "toolArgs", "toolInput"),
            Cwd = ReadString(root, "cwd", "workingDirectory"),
        };

        return true;
    }

    /// <summary>
    /// Flattens every string leaf of the tool arguments into one buffer for content scanning.
    /// </summary>
    /// <remarks>
    /// The argument shape differs per tool and per client (<c>content</c> for writes,
    /// <c>old_string</c>/<c>new_string</c> for edits, <c>command</c> for shells, and a single
    /// patch blob for Codex's <c>apply_patch</c>), so detection reads all string values rather
    /// than enumerating every client's schema.
    /// </remarks>
    public string GetInputText()
    {
        if (ToolInput is not { } input)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        AppendStrings(input, builder, depth: 0);
        return builder.ToString();
    }

    /// <summary>
    /// Returns the file path the tool acted on, when the arguments name one.
    /// </summary>
    public string? GetFilePath()
    {
        if (ToolInput is not { ValueKind: JsonValueKind.Object } input)
        {
            return null;
        }

        return ReadString(input, "file_path", "filePath", "path", "notebook_path");
    }

    private static void AppendStrings(JsonElement element, StringBuilder builder, int depth)
    {
        // Tool arguments are shallow in practice; the cap only guards against pathological input.
        if (depth > 8)
        {
            return;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                builder.AppendLine(element.GetString());
                break;

            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    AppendStrings(property.Value, builder, depth + 1);
                }

                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    AppendStrings(item, builder, depth + 1);
                }

                break;
        }
    }

    private static JsonElement? ReadProperty(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var value) && value.ValueKind is not JsonValueKind.Null)
            {
                // Copilot documents tool arguments as "parsed from JSON string when possible", so a
                // client that still sends the unparsed string gets one parse attempt here.
                if (value.ValueKind is JsonValueKind.String && value.GetString() is { } raw)
                {
                    try
                    {
                        using var nested = JsonDocument.Parse(raw);
                        return nested.RootElement.Clone();
                    }
                    catch (JsonException)
                    {
                        return value;
                    }
                }

                return value;
            }
        }

        return null;
    }

    private static string? ReadString(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.String)
            {
                return value.GetString();
            }
        }

        return null;
    }
}
