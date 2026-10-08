using System.Text.Json;
using System.Text.Json.Nodes;

namespace DotnetEfCoreMcp.Server.Hooks;

/// <summary>
/// Decides whether a hooked tool call warrants a reminder, and renders the client-specific
/// response envelope.
/// </summary>
/// <remarks>
/// This is the counterpart to the server's <c>ServerInstructions</c>. Instructions are only read
/// when a client loads the server - which, with dynamic tool loading, often never happens - so the
/// same guidance is re-delivered at the moment the agent actually writes a LINQ query.
/// </remarks>
public sealed class HookReminder
{
    /// <summary>LINQ writes allowed before the first reminder.</summary>
    internal const int LinqWriteThreshold = 1;

    /// <summary>Entity reads allowed before the (secondary, quieter) schema reminder.</summary>
    internal const int EntityReadThreshold = 3;

    /// <summary>Minimum gap between reminders in one session, to avoid nagging.</summary>
    internal static readonly TimeSpan ReminderCooldown = TimeSpan.FromMinutes(10);

    private const string LinqReminderText =
        """
        LINQ validation reminder (dotnet-efcore-mcp): you just wrote or changed an EF Core LINQ
        query. LINQ that compiles and runs fine in memory can still fail at runtime with an EF Core
        translation error. Before you finish, validate the query against the real EF Core model:

        - `preview_query_sql` - translate the query and see the SQL without touching the database.
        - `run_query` - execute it read-only against the configured connection.
        - `get_entity_schema` / `search_schema` - confirm entity, navigation, and property names.

        If the dotnet-efcore-mcp tools are not loaded yet, load them now rather than skipping this.
        """;

    private const string EntityReminderText =
        """
        Schema discovery reminder (dotnet-efcore-mcp): you are reading EF Core entity source to work
        out the model. The dotnet-efcore-mcp tools answer this directly from the compiled EF Core
        model, including relationships and database column types that the source does not show:

        - `search_schema` - find entities and properties by name.
        - `get_entity_schema` - full shape of a single entity, including navigations and keys.
        - `list_entities` - enumerate the entities a DbContext exposes.
        """;

    private readonly HookSessionStore _store;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Creates a reminder using the given state store and clock.
    /// </summary>
    public HookReminder(HookSessionStore? store = null, TimeProvider? timeProvider = null)
    {
        _store = store ?? new HookSessionStore();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Evaluates a hook payload and returns the JSON to write to stdout. Returns <c>{}</c> when no
    /// reminder is due, which every supported client treats as "no action".
    /// </summary>
    public string Evaluate(string? payload, HookClient client)
    {
        if (!HookToolCall.TryParse(payload, out var call) || call is null)
        {
            return EmptyResponse;
        }

        var kind = ToolCallClassifier.Classify(call);
        if (kind is ToolCallKind.Unrelated)
        {
            return EmptyResponse;
        }

        var state = _store.Load(call.SessionId);

        // Using the MCP server is the behavior the reminder exists to produce, so it clears the
        // backlog and the cooldown: the agent has already validated against the real model.
        if (kind is ToolCallKind.EfCoreMcpToolUse)
        {
            state.PendingLinqWrites = 0;
            state.PendingEntityReads = 0;
            state.LastReminderAt = null;
            _store.Save(call.SessionId, state);
            return EmptyResponse;
        }

        if (kind is ToolCallKind.LinqWrite)
        {
            state.PendingLinqWrites++;
        }
        else
        {
            state.PendingEntityReads++;
        }

        var now = _timeProvider.GetUtcNow();
        var reminder = SelectReminder(state, now);

        if (reminder is null)
        {
            _store.Save(call.SessionId, state);
            return EmptyResponse;
        }

        state.LastReminderAt = now;
        if (reminder == LinqReminderText)
        {
            state.PendingLinqWrites = 0;
        }
        else
        {
            state.PendingEntityReads = 0;
        }

        _store.Save(call.SessionId, state);

        return BuildResponse(reminder, client);
    }

    /// <summary>Clears all reminder state for the session named in the payload.</summary>
    public string Cleanup(string? payload)
    {
        if (HookToolCall.TryParse(payload, out var call) && call is not null)
        {
            _store.Clear(call.SessionId);
            return EmptyResponse;
        }

        // Session-end payloads carry no tool name, so fall back to reading just the session id.
        var sessionId = ReadSessionId(payload);
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            _store.Clear(sessionId);
        }

        return EmptyResponse;
    }

    private static string EmptyResponse => "{}";

    private static string? SelectReminder(HookSessionState state, DateTimeOffset now)
    {
        if (state.LastReminderAt is { } last && now - last < ReminderCooldown)
        {
            return null;
        }

        // LINQ validation is the primary goal, so it wins whenever both are pending.
        if (state.PendingLinqWrites >= LinqWriteThreshold)
        {
            return LinqReminderText;
        }

        return state.PendingEntityReads >= EntityReadThreshold ? EntityReminderText : null;
    }

    /// <summary>
    /// Builds a response carrying the reminder in every envelope the supported clients accept.
    /// </summary>
    /// <remarks>
    /// Copilot CLI reads a top-level <c>additionalContext</c>; Claude Code and Codex read
    /// <c>hookSpecificOutput.additionalContext</c>. All three ignore fields they do not recognize,
    /// so emitting both keeps one code path correct even if a config is installed for one client
    /// and later reused by another.
    /// </remarks>
    private static string BuildResponse(string reminder, HookClient client)
    {
        var response = new JsonObject
        {
            ["additionalContext"] = reminder,
            ["hookSpecificOutput"] = new JsonObject
            {
                ["hookEventName"] = "PostToolUse",
                ["additionalContext"] = reminder,
            },
        };

        // Copilot surfaces a dedicated status line for command hooks; harmless elsewhere.
        if (client is HookClient.Copilot)
        {
            response["systemMessage"] = "dotnet-efcore-mcp: reminded the agent to validate LINQ.";
        }

        return response.ToJsonString();
    }

    private static string? ReadSessionId(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind is not JsonValueKind.Object)
            {
                return null;
            }

            foreach (var name in (string[])["session_id", "sessionId"])
            {
                if (document.RootElement.TryGetProperty(name, out var value)
                    && value.ValueKind is JsonValueKind.String)
                {
                    return value.GetString();
                }
            }
        }
        catch (JsonException)
        {
            // Unparseable payloads simply leave state to expire on disk.
        }

        return null;
    }
}
