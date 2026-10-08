using System.Text.RegularExpressions;

namespace DotnetEfCoreMcp.Server.Hooks;

/// <summary>
/// What a hooked tool call means for reminder purposes.
/// </summary>
public enum ToolCallKind
{
    /// <summary>Nothing of interest; counters are left untouched.</summary>
    Unrelated,

    /// <summary>A write that introduces or changes an EF Core LINQ query.</summary>
    LinqWrite,

    /// <summary>A read of a file that looks like an EF entity or <c>DbContext</c>.</summary>
    EntityRead,

    /// <summary>A call to this MCP server, which satisfies the nudge and resets counters.</summary>
    EfCoreMcpToolUse,
}

/// <summary>
/// Classifies <c>PostToolUse</c> payloads so the reminder only fires for calls that actually write
/// EF Core LINQ or browse the EF model.
/// </summary>
/// <remarks>
/// Precision matters more than recall here. A reminder that fires on every <c>List&lt;T&gt;.Where()</c>
/// would train the agent (and the user) to ignore it, so LINQ detection requires both a query
/// operator and an EF-specific signal, and skips files where validation does not apply.
/// </remarks>
public static partial class ToolCallClassifier
{
    private static readonly string[] WriteTools =
    [
        // Claude Code / Copilot "VS Code compatible" names, Copilot runtime names, and the Codex
        // patch tool (which Codex also lets configs match as Edit/Write).
        "write", "edit", "multiedit", "notebookedit", "create", "apply_patch", "str_replace_editor",
    ];

    private static readonly string[] ShellTools = ["bash", "powershell", "shell", "run_in_terminal"];

    private static readonly string[] ReadTools = ["read", "view", "notebookread"];

    /// <summary>
    /// Shell commands that compile or run the target app, where an untranslatable query surfaces.
    /// </summary>
    private static readonly string[] BuildCommandMarkers =
    [
        "dotnet run", "dotnet test", "dotnet build", "dotnet watch", "dotnet ef",
    ];

    /// <summary>
    /// LINQ query operators. Present in both EF and in-memory LINQ, so never sufficient alone.
    /// </summary>
    private static readonly string[] LinqOperators =
    [
        ".where(", ".select(", ".selectmany(", ".orderby(", ".orderbydescending(", ".thenby(",
        ".groupby(", ".join(", ".firstordefault(", ".singleordefault(", ".anyasync(", ".allasync(",
        ".tolistasync(", ".firstordefaultasync(", ".singleordefaultasync(", ".countasync(",
        ".sumasync(", ".averageasync(", ".maxasync(", ".minasync(", ".toarrayasync(",
        ".todictionaryasync(", ".include(", ".theninclude(",
    ];

    /// <summary>
    /// Signals that a query runs against an EF provider rather than an in-memory sequence.
    /// </summary>
    private static readonly string[] EfCoreSignals =
    [
        "dbset<", "iqueryable", "dbcontext", ".include(", ".theninclude(", ".asnotracking(",
        ".astracking(", ".assplitquery(", ".assinglequery(", ".executeupdateasync(",
        ".executedeleteasync(", ".fromsql", "entityframeworkcore", ".tolistasync(",
        ".firstordefaultasync(", ".singleordefaultasync(", ".anyasync(", ".countasync(",
    ];

    /// <summary>
    /// Path fragments where a LINQ reminder is noise rather than signal.
    /// </summary>
    private static readonly string[] ExcludedPathFragments =
    [
        "/migrations/", "\\migrations\\", ".designer.cs", "modelsnapshot.cs",
        "/obj/", "\\obj\\", "/bin/", "\\bin\\", "/node_modules/", "\\node_modules\\",
    ];

    /// <summary>
    /// Classifies a tool call.
    /// </summary>
    public static ToolCallKind Classify(HookToolCall call)
    {
        var toolName = call.ToolName.Trim().ToLowerInvariant();

        if (IsEfCoreMcpTool(toolName))
        {
            return ToolCallKind.EfCoreMcpToolUse;
        }

        var filePath = call.GetFilePath();

        if (WriteTools.Contains(toolName))
        {
            if (IsExcludedPath(filePath))
            {
                return ToolCallKind.Unrelated;
            }

            // A write only matters when it lands in C#; the patch tools carry the path inside the
            // patch body, so fall back to scanning the arguments when no path is reported.
            var text = call.GetInputText();
            if (!LooksLikeCSharp(filePath, text))
            {
                return ToolCallKind.Unrelated;
            }

            return ContainsEfCoreLinq(text) ? ToolCallKind.LinqWrite : ToolCallKind.Unrelated;
        }

        if (ShellTools.Contains(toolName))
        {
            var text = call.GetInputText();
            return IsEfCoreBuildCommand(text) ? ToolCallKind.LinqWrite : ToolCallKind.Unrelated;
        }

        if (ReadTools.Contains(toolName) && LooksLikeEntityFile(filePath))
        {
            return ToolCallKind.EntityRead;
        }

        return ToolCallKind.Unrelated;
    }

    /// <summary>
    /// Whether a text blob contains an EF Core LINQ query, requiring both a query operator and an
    /// EF-specific signal so that in-memory LINQ over collections does not trigger a reminder.
    /// </summary>
    public static bool ContainsEfCoreLinq(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var normalized = text.ToLowerInvariant();

        var hasOperator = LinqOperators.Any(normalized.Contains)
            || QuerySyntaxRegex().IsMatch(normalized);

        if (!hasOperator)
        {
            return false;
        }

        return EfCoreSignals.Any(normalized.Contains) || ContextMemberRegex().IsMatch(normalized);
    }

    private static bool IsEfCoreMcpTool(string toolName)
    {
        // MCP tool calls arrive namespaced as mcp__<server>__<tool>; the server name is chosen by
        // the user's client config, so match this server's distinctive tool names too.
        if (toolName.StartsWith("mcp__", StringComparison.Ordinal)
            && (toolName.Contains("efcore", StringComparison.Ordinal)
                || toolName.Contains("ef_core", StringComparison.Ordinal)))
        {
            return true;
        }

        return toolName is "run_query" or "preview_query_sql" or "run_sql_query"
            or "get_schema" or "search_schema" or "get_entity_schema" or "list_entities";
    }

    private static bool IsEfCoreBuildCommand(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var normalized = text.ToLowerInvariant();
        return BuildCommandMarkers.Any(normalized.Contains);
    }

    private static bool LooksLikeCSharp(string? filePath, string text)
    {
        if (!string.IsNullOrWhiteSpace(filePath))
        {
            return filePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                || filePath.EndsWith(".razor", StringComparison.OrdinalIgnoreCase);
        }

        // apply_patch-style payloads embed the target path in the patch body.
        return text.Contains(".cs", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeEntityFile(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || IsExcludedPath(filePath))
        {
            return false;
        }

        if (!filePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var normalized = filePath.Replace('\\', '/').ToLowerInvariant();

        return normalized.Contains("dbcontext")
            || normalized.Contains("/entities/")
            || normalized.Contains("/models/")
            || normalized.Contains("/domain/")
            || normalized.Contains("/data/")
            || normalized.Contains("/persistence/")
            || normalized.Contains("configuration.cs");
    }

    private static bool IsExcludedPath(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return false;
        }

        var normalized = filePath.Replace('\\', '/').ToLowerInvariant();
        return ExcludedPathFragments.Any(fragment =>
            normalized.Contains(fragment.Replace('\\', '/'), StringComparison.Ordinal));
    }

    /// <summary>Matches LINQ query-comprehension syntax, e.g. <c>from o in context.Orders</c>.</summary>
    [GeneratedRegex(@"\bfrom\s+\w+\s+in\s+", RegexOptions.CultureInvariant)]
    private static partial Regex QuerySyntaxRegex();

    /// <summary>Matches a query rooted on a context-like member, e.g. <c>_context.Orders</c>.</summary>
    [GeneratedRegex(@"(_?(db)?context|_?db)\s*\.\s*[a-z]\w*", RegexOptions.CultureInvariant)]
    private static partial Regex ContextMemberRegex();
}
