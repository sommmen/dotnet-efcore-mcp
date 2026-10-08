using System.Text.Json;

namespace DotnetEfCoreMcp.Server.Hooks;

/// <summary>
/// Reminder bookkeeping for a single agent session.
/// </summary>
public sealed class HookSessionState
{
    /// <summary>Unvalidated LINQ writes seen since the last reminder or MCP tool call.</summary>
    public int PendingLinqWrites { get; set; }

    /// <summary>Entity-file reads seen since the last reminder or MCP tool call.</summary>
    public int PendingEntityReads { get; set; }

    /// <summary>When the last reminder was emitted, used to enforce the cooldown.</summary>
    public DateTimeOffset? LastReminderAt { get; set; }
}

/// <summary>
/// Persists <see cref="HookSessionState"/> between hook invocations.
/// </summary>
/// <remarks>
/// Each hook invocation is a separate short-lived process, so the counters that make the reminder
/// rate-limited rather than constant have to live on disk. State is keyed by the client's session
/// id and removed by <c>hooks cleanup</c> at session end. Every operation is best-effort: if the
/// state directory is unwritable the reminder degrades to stateless behavior instead of failing.
/// </remarks>
public sealed class HookSessionStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    private readonly string _rootDirectory;

    /// <summary>
    /// Creates a store rooted at <paramref name="rootDirectory"/>, defaulting to a per-user
    /// application-data directory.
    /// </summary>
    public HookSessionStore(string? rootDirectory = null)
    {
        _rootDirectory = rootDirectory ?? DefaultRootDirectory();
    }

    /// <summary>The default state root, overridable via <c>DOTNETEFCOREMCP_HOOKDATADIR</c>.</summary>
    public static string DefaultRootDirectory()
    {
        var configured = Environment.GetEnvironmentVariable("DOTNETEFCOREMCP_HOOKDATADIR");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        // SpecialFolder.LocalApplicationData can be empty on some Unix configurations.
        if (string.IsNullOrWhiteSpace(appData))
        {
            appData = Path.Combine(Path.GetTempPath(), "dotnet-efcore-mcp-state");
        }

        return Path.Combine(appData, "dotnet-efcore-mcp", "hook-data");
    }

    /// <summary>Loads the state for a session, returning a fresh instance when none exists.</summary>
    public HookSessionState Load(string sessionId)
    {
        var path = GetStatePath(sessionId);

        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var state = JsonSerializer.Deserialize<HookSessionState>(json, SerializerOptions);
                if (state is not null)
                {
                    return state;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // Corrupt or unreadable state is equivalent to no state.
        }

        return new HookSessionState();
    }

    /// <summary>Saves the state for a session, ignoring failures.</summary>
    public void Save(string sessionId, HookSessionState state)
    {
        var path = GetStatePath(sessionId);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(state, SerializerOptions));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Losing a counter update only costs one reminder; never surface it to the agent.
        }
    }

    /// <summary>Deletes all state for a session.</summary>
    public void Clear(string sessionId)
    {
        try
        {
            var directory = Path.GetDirectoryName(GetStatePath(sessionId))!;
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Stale state is harmless; it is scoped to a session id that will not recur.
        }
    }

    private string GetStatePath(string sessionId) =>
        Path.Combine(_rootDirectory, Sanitize(sessionId), "state.json");

    /// <summary>
    /// Reduces a client-supplied session id to a safe directory name.
    /// </summary>
    private static string Sanitize(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return "default";
        }

        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string([.. sessionId.Select(c => invalid.Contains(c) ? '_' : c)]);

        // Session ids are client-controlled, so collapse any traversal attempt to a literal name.
        sanitized = sanitized.Replace(".", "_", StringComparison.Ordinal).Trim();

        return string.IsNullOrEmpty(sanitized)
            ? "default"
            : sanitized[..Math.Min(sanitized.Length, 128)];
    }
}
