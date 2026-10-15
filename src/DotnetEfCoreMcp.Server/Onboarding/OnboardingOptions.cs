namespace DotnetEfCoreMcp.Server.Onboarding;

/// <summary>
/// Controls whether the <c>onboard</c> MCP tool may write onboarding files.
/// </summary>
/// <remarks>
/// <para>
/// Reporting status over MCP is always allowed - it only reads, and it is what makes the hooks and
/// skills discoverable from inside a session in the first place. *Installing* over MCP is gated,
/// mirroring <see cref="Mutations.EntityMutationsOptions"/>: every other tool in this server that
/// writes outside the process is off until an operator explicitly turns it on.
/// </para>
/// <para>
/// The gate matters more here than the file count suggests. An installed hook adds a command that
/// the agent's CLI executes on every subsequent tool call, so letting an agent write one is a
/// broader grant than letting it write a row. A tool description asking the agent to confirm with
/// the user is a prompt, not an enforcement boundary, and prompts are exactly what an injected
/// instruction can talk its way past.
/// </para>
/// <para>
/// Leaving this off costs little discoverability: the agent can still read and report what is
/// available and hand the user the exact CLI command, which keeps the human in the loop by running
/// the install themselves.
/// </para>
/// </remarks>
public sealed class OnboardingOptions
{
    /// <summary>
    /// Gets whether the <c>onboard</c> tool may install hooks and skills. Defaults to
    /// <see langword="false"/>.
    /// </summary>
    public bool AllowInstall { get; init; }
}
