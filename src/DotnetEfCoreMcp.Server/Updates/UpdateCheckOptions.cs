namespace DotnetEfCoreMcp.Server.Updates;

/// <summary>
/// Server-side configuration for the update check, bound from the <c>UpdateCheck</c> section.
/// </summary>
public sealed class UpdateCheckOptions
{
    /// <summary>
    /// Whether the server may check for a newer published version. Defaults to enabled.
    /// </summary>
    /// <remarks>
    /// On by default because the check is an unauthenticated GET for a public package's version
    /// list on the feed this package is published to - no user data, no payload, the same request a
    /// human makes by opening the package page. Users who still want it off have this setting and
    /// the <see cref="UpdateCheckGate.DisableVariable"/> environment variable.
    /// </remarks>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Flat container base address to query, defaulting to nuget.org.
    /// </summary>
    /// <remarks>
    /// Exists so someone on a private mirror can point the check at a feed they can actually
    /// install from. Without it, the check could advertise a version their sources do not carry.
    /// </remarks>
    public string? Source { get; set; }
}
