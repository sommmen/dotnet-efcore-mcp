using System.Text.Json;
using System.Text.Json.Nodes;

namespace DotnetEfCoreMcp.Server.Hooks;

/// <summary>
/// The outcome of installing or removing hooks for one client.
/// </summary>
/// <param name="Client">The client the result refers to.</param>
/// <param name="FilePath">The config file that was inspected or written.</param>
/// <param name="Changed">Whether the file's contents changed.</param>
/// <param name="Message">A human-readable summary for CLI output.</param>
public sealed record HookInstallResult(HookClient Client, string FilePath, bool Changed, string Message);

/// <summary>
/// Writes repo-local hook configuration for the supported agent CLIs.
/// </summary>
/// <remarks>
/// All three clients are configured with the PascalCase <c>PostToolUse</c> event: Claude Code and
/// Codex use it natively, and Copilot CLI explicitly supports PascalCase event names with Claude
/// matcher semantics. Using one dialect everywhere keeps a single matcher vocabulary and a single
/// payload parser.
/// <para>
/// <c>PostToolUse</c> is deliberate rather than <c>PreToolUse</c>: Copilot CLI's <c>preToolUse</c>
/// command hooks are fail-closed, so a crashing or slow reminder would deny the agent's file write.
/// A reminder must never be able to block the user's edit.
/// </para>
/// </remarks>
public sealed class HookInstaller
{
    /// <summary>Identifies hooks this installer owns, so upgrades replace rather than duplicate them.</summary>
    internal const string ManagedPackageId = "DotnetEfCoreMcp.Server";

    /// <summary>Marker property written by earlier builds, still recognized when upgrading.</summary>
    private const string ManagedMarker = "dotnet-efcore-mcp:linq-validation";

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private readonly string _toolCommand;
    private readonly string[] _toolArguments;

    /// <summary>
    /// Creates an installer that wires hooks to the given command.
    /// </summary>
    /// <param name="packageVersion">
    /// Version to pin the <c>dnx</c> invocation to. When omitted, the hook resolves the latest
    /// package each run.
    /// </param>
    /// <param name="command">Overrides the executable, for tests or local builds.</param>
    /// <param name="arguments">Overrides the leading arguments, for tests or local builds.</param>
    public HookInstaller(string? packageVersion = null, string? command = null, string[]? arguments = null)
    {
        _toolCommand = command ?? "dnx";

        if (arguments is not null)
        {
            _toolArguments = arguments;
        }
        else
        {
            // --yes keeps the hook non-interactive; dnx would otherwise prompt on first resolve and
            // hang until the client's hook timeout fires.
            _toolArguments = string.IsNullOrWhiteSpace(packageVersion)
                // Without a pinned version, dnx resolves the latest *stable* release. Releases are
                // preview-only today, so an unpinned command needs --prerelease to resolve at all.
                ? [ManagedPackageId, "--yes", "--prerelease", "--"]
                : [$"{ManagedPackageId}@{packageVersion}", "--yes", "--"];
        }
    }

    /// <summary>Installs hooks for a client into <paramref name="repositoryRoot"/>.</summary>
    public HookInstallResult Install(HookClient client, string repositoryRoot, bool force = false)
    {
        var path = GetConfigPath(client, repositoryRoot);
        var existing = ReadJsonObject(path);

        if (existing is null)
        {
            return new HookInstallResult(client, path, Changed: false,
                $"Skipped {path}: the file exists but is not a JSON object. Fix or remove it, then retry.");
        }

        var alreadyManaged = HasManagedHook(existing, client);
        if (alreadyManaged && !force)
        {
            var refreshed = ApplyHooks(CloneWithoutManagedHooks(existing, client), client);
            if (JsonNode.DeepEquals(refreshed, existing))
            {
                return new HookInstallResult(client, path, Changed: false,
                    $"Already up to date: {path}");
            }
        }

        var updated = ApplyHooks(CloneWithoutManagedHooks(existing, client), client);
        WriteJson(path, updated);

        return new HookInstallResult(client, path, Changed: true,
            alreadyManaged ? $"Updated {path}" : $"Installed {path}");
    }

    /// <summary>Removes this installer's hooks for a client, leaving unrelated hooks intact.</summary>
    public HookInstallResult Uninstall(HookClient client, string repositoryRoot)
    {
        var path = GetConfigPath(client, repositoryRoot);

        if (!File.Exists(path))
        {
            return new HookInstallResult(client, path, Changed: false, $"Nothing to remove: {path}");
        }

        var existing = ReadJsonObject(path);
        if (existing is null)
        {
            return new HookInstallResult(client, path, Changed: false,
                $"Skipped {path}: the file is not a JSON object.");
        }

        if (!HasManagedHook(existing, client))
        {
            return new HookInstallResult(client, path, Changed: false,
                $"No dotnet-efcore-mcp hooks found in {path}");
        }

        var cleaned = CloneWithoutManagedHooks(existing, client);

        // A file we created solely for our hooks is removed rather than left as an empty husk.
        if (IsEffectivelyEmpty(cleaned, client))
        {
            File.Delete(path);
            return new HookInstallResult(client, path, Changed: true, $"Removed {path}");
        }

        WriteJson(path, cleaned);
        return new HookInstallResult(client, path, Changed: true, $"Updated {path}");
    }

    /// <summary>Reports whether a client currently has this installer's hooks.</summary>
    public HookInstallResult Status(HookClient client, string repositoryRoot)
    {
        var path = GetConfigPath(client, repositoryRoot);

        if (!File.Exists(path))
        {
            return new HookInstallResult(client, path, Changed: false, "not installed");
        }

        var existing = ReadJsonObject(path);
        if (existing is null)
        {
            return new HookInstallResult(client, path, Changed: false, "unreadable (not a JSON object)");
        }

        return new HookInstallResult(client, path, Changed: false,
            HasManagedHook(existing, client) ? "installed" : "not installed");
    }

    /// <summary>Resolves the repo-local config file a client reads.</summary>
    public static string GetConfigPath(HookClient client, string repositoryRoot) => client switch
    {
        HookClient.Claude => Path.Combine(repositoryRoot, ".claude", "settings.json"),

        // A dedicated file under .github/hooks avoids merge conflicts with unrelated Copilot hooks.
        HookClient.Copilot => Path.Combine(repositoryRoot, ".github", "hooks", "dotnet-efcore-mcp.json"),
        HookClient.Codex => Path.Combine(repositoryRoot, ".codex", "hooks.json"),
        _ => throw new ArgumentOutOfRangeException(nameof(client), client, "Unsupported hook client."),
    };

    private JsonObject ApplyHooks(JsonObject root, HookClient client)
    {
        if (client is HookClient.Copilot)
        {
            // Copilot requires an explicit schema version and uses a flat entry list per event.
            root["version"] = 1;
        }

        var hooks = root["hooks"] as JsonObject;
        if (hooks is null)
        {
            hooks = [];
            root["hooks"] = hooks;
        }

        AddEntry(hooks, client, "PostToolUse", "remind", matcher: PostToolUseMatcher);
        AddEntry(hooks, client, "SessionEnd", "cleanup", matcher: null);

        return root;
    }

    /// <summary>
    /// The tools whose results should be inspected for LINQ.
    /// </summary>
    /// <remarks>
    /// Both casings are listed because the clients disagree on which name reaches the matcher:
    /// Claude Code matches its own PascalCase tool names, Copilot CLI compiles the pattern as a
    /// regex against runtime names that are lowercase (reporting Claude names only for some
    /// PascalCase-configured events), and Codex matches <c>apply_patch</c> under any of three
    /// aliases. Listing every spelling keeps one matcher correct everywhere; a name that a given
    /// client never emits simply never matches.
    /// </remarks>
    private const string PostToolUseMatcher =
        "Write|Edit|MultiEdit|NotebookEdit|Bash|PowerShell|Read|View|"
        + "write|edit|multiedit|create|bash|powershell|read|view|apply_patch";

    private void AddEntry(JsonObject hooks, HookClient client, string eventName, string verb, string? matcher)
    {
        // Only fields the clients document are emitted. A custom marker property would be a
        // tidier way to recognize our own hooks, but Copilot CLI drops hook items that fail
        // validation and Codex hashes the hook definition for its trust prompt, so an unknown
        // field risks silently disabling the hook. Ownership is detected from the command text
        // instead, which is unambiguous because it names this package.
        var handler = new JsonObject
        {
            ["type"] = "command",
        };

        if (client is HookClient.Copilot)
        {
            // exec/args runs the executable directly, so Windows paths and quoting never hit a shell.
            handler["exec"] = _toolCommand;
            handler["args"] = new JsonArray([.. BuildArguments(verb, client).Select(a => (JsonNode)JsonValue.Create(a))]);
            handler["timeoutSec"] = HookTimeoutSeconds;
        }
        else
        {
            handler["command"] = BuildCommandLine(verb, client);
            handler["timeout"] = HookTimeoutSeconds;
        }

        if (client is HookClient.Copilot)
        {
            // Copilot's file format lists handlers directly under the event, with the matcher on
            // the handler itself rather than in a wrapping group.
            if (!string.IsNullOrEmpty(matcher))
            {
                handler["matcher"] = matcher;
            }

            GetOrCreateArray(hooks, eventName).Add(handler);
            return;
        }

        var group = new JsonObject
        {
            ["matcher"] = matcher ?? string.Empty,
            ["hooks"] = new JsonArray(handler),
        };

        GetOrCreateArray(hooks, eventName).Add(group);
    }

    private string[] BuildArguments(string verb, HookClient client) =>
        [.. _toolArguments, "hooks", verb, "--client", ClientName(client)];

    private string BuildCommandLine(string verb, HookClient client)
    {
        var parts = new List<string> { _toolCommand };
        parts.AddRange(BuildArguments(verb, client));

        return string.Join(' ', parts.Select(part => part.Contains(' ', StringComparison.Ordinal) ? $"\"{part}\"" : part));
    }

    private static JsonArray GetOrCreateArray(JsonObject parent, string name)
    {
        if (parent[name] is JsonArray existing)
        {
            return existing;
        }

        var created = new JsonArray();
        parent[name] = created;
        return created;
    }

    private static JsonObject CloneWithoutManagedHooks(JsonObject root, HookClient client)
    {
        var clone = root.DeepClone().AsObject();

        if (clone["hooks"] is not JsonObject hooks)
        {
            return clone;
        }

        foreach (var eventName in hooks.Select(pair => pair.Key).ToList())
        {
            if (hooks[eventName] is not JsonArray entries)
            {
                continue;
            }

            for (var index = entries.Count - 1; index >= 0; index--)
            {
                if (IsManagedEntry(entries[index], client))
                {
                    entries.RemoveAt(index);
                }
            }

            if (entries.Count == 0)
            {
                hooks.Remove(eventName);
            }
        }

        // Don't leave an empty "hooks": {} behind in a file that holds unrelated settings.
        if (hooks.Count == 0)
        {
            clone.Remove("hooks");
        }

        return clone;
    }

    private static bool IsManagedEntry(JsonNode? entry, HookClient client)
    {
        if (entry is not JsonObject entryObject)
        {
            return false;
        }

        if (IsManagedHandler(entryObject))
        {
            return true;
        }

        // Claude/Codex nest handlers inside a matcher group; a group is ours only when every
        // handler in it is, so a user's handler added to our group is never silently discarded.
        if (client is not HookClient.Copilot && entryObject["hooks"] is JsonArray handlers)
        {
            for (var index = handlers.Count - 1; index >= 0; index--)
            {
                if (IsManagedHandler(handlers[index] as JsonObject))
                {
                    handlers.RemoveAt(index);
                }
            }

            return handlers.Count == 0;
        }

        return false;
    }

    /// <summary>
    /// Whether a handler was written by this installer, identified by the package it invokes.
    /// </summary>
    private static bool IsManagedHandler(JsonObject? handler)
    {
        if (handler is null)
        {
            return false;
        }

        // An older build tagged handlers with a custom property; still honor it when upgrading.
        if (handler[ManagedKey]?.GetValue<string>() == ManagedMarker)
        {
            return true;
        }

        if (Mentions(handler["command"]))
        {
            return true;
        }

        return handler["args"] is JsonArray args && args.Any(Mentions);

        static bool Mentions(JsonNode? node) =>
            node is JsonValue value
            && value.TryGetValue<string>(out var text)
            && text.Contains(ManagedPackageId, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasManagedHook(JsonObject root, HookClient client)
    {
        if (root["hooks"] is not JsonObject hooks)
        {
            return false;
        }

        // Probe a throwaway clone so detection never mutates the caller's document.
        var clone = root.DeepClone().AsObject();
        var cleaned = CloneWithoutManagedHooks(clone, client);

        return !JsonNode.DeepEquals(cleaned["hooks"], hooks);
    }

    private static bool IsEffectivelyEmpty(JsonObject root, HookClient client)
    {
        if (root["hooks"] is JsonObject hooks && hooks.Count > 0)
        {
            return false;
        }

        // "version" and "hooks" are scaffolding this installer adds; anything else is the user's.
        return root.All(pair => pair.Key is "hooks" || (client is HookClient.Copilot && pair.Key is "version"));
    }

    private static JsonObject? ReadJsonObject(string path)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            var text = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(text))
            {
                return [];
            }

            // Claude settings files are routinely hand-edited, so tolerate comments and trailing commas.
            var node = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });

            return node as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void WriteJson(string path, JsonObject root)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, root.ToJsonString(WriteOptions) + Environment.NewLine);
    }

    /// <summary>
    /// Keeps a slow package resolve from stalling the agent. Copilot's own default is 30 s; the
    /// reminder is advisory, so failing fast is better than making the agent wait.
    /// </summary>
    private const int HookTimeoutSeconds = 20;

    private const string ManagedKey = "x-dotnet-efcore-mcp";

    internal static string ClientName(HookClient client) => client switch
    {
        HookClient.Claude => "claude",
        HookClient.Copilot => "copilot",
        HookClient.Codex => "codex",
        _ => throw new ArgumentOutOfRangeException(nameof(client), client, "Unsupported hook client."),
    };
}
