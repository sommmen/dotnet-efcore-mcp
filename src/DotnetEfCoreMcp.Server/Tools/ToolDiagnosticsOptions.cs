using Microsoft.Extensions.Configuration;

namespace DotnetEfCoreMcp.Server.Tools;

/// <summary>Controls the additional development-only diagnostic metadata returned for unexpected
/// MCP tool failures. Stack traces are never returned.
/// <para>The underlying failure cause is surfaced to callers regardless of this setting: EF Core
/// translation and provider SQL diagnostics describe the query rather than the data, and they are
/// what lets a calling agent correct itself (issue #85). Credential material is stripped from every
/// returned message by <see cref="SensitiveTextRedactor"/>.</para></summary>
public sealed class ToolDiagnosticsOptions
{
    /// <summary>Additionally names the <em>outer</em> exception type ("Failure category") for
    /// unexpected tool failures. The flattened cause deliberately reports only the innermost
    /// exception, so this is informative when the wrapper itself is the interesting part - an
    /// assembly load failure, for example. Effective only when the server host environment is
    /// <c>Development</c>; the correlation identifier and remediation hint are always returned.</summary>
    public bool ExposeSafeErrorDetails { get; init; }

    /// <summary>Creates the effective options, ensuring a configuration value cannot enable
    /// diagnostic metadata outside the Development host environment.</summary>
    public static ToolDiagnosticsOptions CreateEffective(IConfiguration configuration, bool isDevelopment)
    {
        var configured = configuration.GetSection("ToolDiagnostics").Get<ToolDiagnosticsOptions>() ?? new ToolDiagnosticsOptions();
        return new ToolDiagnosticsOptions
        {
            ExposeSafeErrorDetails = isDevelopment && configured.ExposeSafeErrorDetails,
        };
    }
}
