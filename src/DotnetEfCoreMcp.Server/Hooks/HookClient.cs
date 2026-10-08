namespace DotnetEfCoreMcp.Server.Hooks;

/// <summary>
/// The agent CLI a hook invocation belongs to. Each client has its own config file location and
/// output envelope, but they all share the PascalCase event/matcher dialect this server installs.
/// </summary>
public enum HookClient
{
    /// <summary>Anthropic Claude Code (<c>.claude/settings.json</c>).</summary>
    Claude,

    /// <summary>GitHub Copilot CLI (<c>.github/hooks/*.json</c>).</summary>
    Copilot,

    /// <summary>OpenAI Codex CLI (<c>.codex/hooks.json</c>).</summary>
    Codex,
}
