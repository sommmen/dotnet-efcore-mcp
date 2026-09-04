using Microsoft.Extensions.Configuration;

namespace DotnetEfCoreMcp.Server.Tools;

/// <summary>Controls the development-only diagnostic metadata returned for unexpected MCP tool
/// failures. Exception messages and stack traces are never returned because providers can include
/// connection strings, SQL, server names, or other sensitive data in them.</summary>
public sealed class ToolDiagnosticsOptions
{
    /// <summary>Enables a correlation identifier, exception type, and vetted remediation hint for
    /// unexpected tool failures. This is effective only when the server host environment is
    /// <c>Development</c>.</summary>
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
