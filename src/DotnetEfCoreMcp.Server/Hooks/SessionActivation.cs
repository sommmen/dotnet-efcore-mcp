using System.Text.Json;
using System.Text.Json.Nodes;
using DotnetEfCoreMcp.Server.Onboarding;

namespace DotnetEfCoreMcp.Server.Hooks;

/// <summary>
/// Builds the <c>SessionStart</c> context that tells an agent this server exists before it has any
/// reason to look for it.
/// </summary>
/// <remarks>
/// <para>
/// This is the discoverability counterpart to <see cref="HookReminder"/>. A reminder can only
/// correct an agent that already started writing LINQ by hand; by then it has usually read half the
/// entity source. Announcing the server at session start means the agent knows the schema tools
/// exist the first time it needs one.
/// </para>
/// <para>
/// It also carries the <em>other</em> discoverability problem: a user who has the MCP server
/// configured usually does not know the feedback skill or the reminder hooks exist at all, because
/// nothing in a normal session mentions them. When something is missing, the context says so and
/// names the one command that installs it.
/// </para>
/// </remarks>
public sealed class SessionActivation
{
    private readonly OnboardingInspector _inspector;

    /// <summary>Creates an activation builder.</summary>
    /// <param name="inspector">Reports which onboarding artifacts are present.</param>
    public SessionActivation(OnboardingInspector? inspector = null) =>
        _inspector = inspector ?? new OnboardingInspector();

    /// <summary>
    /// Evaluates a <c>SessionStart</c> payload and returns the JSON to write to stdout.
    /// </summary>
    /// <param name="payload">The raw hook payload.</param>
    /// <param name="client">The client whose response envelope to emit.</param>
    /// <param name="scope">The scope the hook was installed at.</param>
    /// <returns>
    /// A response carrying <c>additionalContext</c>, or <c>{}</c> when the session has nothing to do
    /// with EF Core.
    /// </returns>
    public string Evaluate(string? payload, HookClient client, InstallScope scope)
    {
        var workspace = ResolveWorkspace(payload);

        // A repo-local hook was installed into this specific repository, which is already an
        // explicit opt-in, so it always announces. A user-level hook fires in every session on the
        // machine - including repositories with no EF Core anywhere - so it stays silent unless the
        // workspace actually looks like an EF Core project. Announcing EF Core tooling in a
        // TypeScript repo would be noise, and noise is what trains agents to ignore context.
        if (scope is InstallScope.User && !EfCoreWorkspaceProbe.LooksLikeEfCoreWorkspace(workspace))
        {
            return "{}";
        }

        var status = _inspector.Inspect(workspace);
        return BuildResponse(BuildContext(status), client);
    }

    private static string BuildContext(OnboardingStatus status)
    {
        var context = new System.Text.StringBuilder();

        context.AppendLine(
            """
            dotnet-efcore-mcp is available in this workspace. It reflects over the compiled EF Core
            model of the target application, so prefer it over reading entity source or writing
            throwaway inspection code:

            - `list_contexts` / `list_entities` / `get_entity_schema` / `search_schema` - the real
              model, including navigations, keys, and database column types the source does not show.
            - `preview_query_sql` - translate a LINQ query to SQL without touching the database.
            - `run_query` - execute a LINQ query read-only against the configured connection.

            LINQ that compiles and reads correctly can still throw at runtime because EF Core cannot
            translate it. Validate any LINQ you write or change with `preview_query_sql` or
            `run_query` before you finish.
            """);

        // Only mention what is actually missing. A session that is fully set up should get the
        // tool guidance above and nothing else, or the setup note becomes background noise that
        // costs context on every single session start.
        var missing = new List<string>();

        if (!status.FeedbackSkillInstalled)
        {
            missing.Add(
                "the feedback skill (turns a bad experience with this server into a self-contained "
                + "GitHub issue, scrubbed of private code)");
        }

        if (!status.HooksInstalled)
        {
            missing.Add("the reminder hooks for the other agent CLIs on this machine");
        }

        if (missing.Count > 0)
        {
            context.AppendLine();
            context.AppendLine(
                $"Optional and not yet installed: {string.Join("; ", missing)}. "
                + "If the user wants either, call the `onboard` tool with `apply: true` "
                + $"(or run `{OnboardingStatus.InstallCommandHint}`). Do not install anything without asking.");
        }

        return context.ToString().TrimEnd();
    }

    /// <summary>
    /// Resolves the directory the session is running in.
    /// </summary>
    /// <remarks>
    /// Clients disagree on the field name, and some omit it entirely on <c>SessionStart</c>, so the
    /// process working directory is the final fallback - hook handlers are launched in the session's
    /// own directory by all three clients.
    /// </remarks>
    private static string ResolveWorkspace(string? payload)
    {
        if (!string.IsNullOrWhiteSpace(payload))
        {
            try
            {
                using var document = JsonDocument.Parse(payload);
                if (document.RootElement.ValueKind is JsonValueKind.Object)
                {
                    foreach (var name in (string[])["cwd", "workspaceRoot", "workspace_root", "projectDir", "project_dir"])
                    {
                        if (document.RootElement.TryGetProperty(name, out var value)
                            && value.ValueKind is JsonValueKind.String
                            && value.GetString() is { Length: > 0 } directory)
                        {
                            return directory;
                        }
                    }
                }
            }
            catch (JsonException)
            {
                // Fall through to the working directory; a malformed payload must not fail the hook.
            }
        }

        return Directory.GetCurrentDirectory();
    }

    private static string BuildResponse(string context, HookClient client)
    {
        var response = new JsonObject
        {
            ["additionalContext"] = context,
            ["hookSpecificOutput"] = new JsonObject
            {
                ["hookEventName"] = "SessionStart",
                ["additionalContext"] = context,
            },
        };

        if (client is HookClient.Copilot)
        {
            response["systemMessage"] = "dotnet-efcore-mcp: EF Core schema and query tools are available.";
        }

        return response.ToJsonString();
    }
}
