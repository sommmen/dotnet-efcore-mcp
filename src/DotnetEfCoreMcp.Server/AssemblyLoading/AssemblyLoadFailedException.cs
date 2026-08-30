namespace DotnetEfCoreMcp.Server.AssemblyLoading;

/// <summary>Thrown when a target assembly fails to load or its dependencies cannot be resolved.
/// Messages on this exception are safe to surface directly to an MCP client - callers must not
/// wrap raw <see cref="System.Exception"/> instances (which may contain file-system details
/// beyond what should be disclosed) without going through this type or otherwise sanitizing.</summary>
public sealed class AssemblyLoadFailedException : Exception
{
    public AssemblyLoadFailedException(string message)
        : base(message)
    {
    }

    public AssemblyLoadFailedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
