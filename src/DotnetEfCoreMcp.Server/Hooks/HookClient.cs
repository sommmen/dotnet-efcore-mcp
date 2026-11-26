namespace DotnetEfCoreMcp.Server.Hooks;

/// <summary>
/// The agent CLI a hook invocation belongs to. Each client has its own config file location and
/// output envelope. Claude Code and Codex use PascalCase event names; Copilot is installed with its
/// native camelCase ones.
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
