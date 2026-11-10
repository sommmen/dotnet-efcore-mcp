using System.Reflection;
using Microsoft.Extensions.Configuration.UserSecrets;

namespace DotnetEfCoreMcp.Server.Connections;

/// <summary>Helpers for the .NET user-secrets store this server reads its "Connections" section
/// from.</summary>
public static class UserSecretsStore
{
    /// <summary>Creates the user-secrets directory for <paramref name="assembly"/>'s
    /// <see cref="UserSecretsIdAttribute"/> if it does not already exist, and returns the resolved
    /// <c>secrets.json</c> path (null when the assembly declares no user-secrets ID).
    ///
    /// This must run *before* <c>AddUserSecrets</c>. That extension resolves the secrets file's
    /// directory and, when the directory is missing, falls back to rooting its
    /// <see cref="Microsoft.Extensions.FileProviders.PhysicalFileProvider"/> at the application base
    /// directory instead. The provider is then permanently unable to see the real secrets file, even
    /// after it is created and even across an explicit <c>IConfigurationRoot.Reload()</c> - so the
    /// connection registry could never pick up a connection configured after startup (issue
    /// #121).</summary>
    public static string? EnsureDirectoryExists(Assembly assembly)
    {
        var secretsId = assembly.GetCustomAttribute<UserSecretsIdAttribute>()?.UserSecretsId;
        if (string.IsNullOrWhiteSpace(secretsId))
        {
            return null;
        }

        string secretsPath;
        try
        {
            secretsPath = PathHelper.GetSecretsPathFromSecretsId(secretsId);
        }
        catch (InvalidOperationException)
        {
            return null;
        }

        var directory = Path.GetDirectoryName(secretsPath);
        if (string.IsNullOrEmpty(directory))
        {
            return null;
        }

        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best-effort: a read-only or otherwise unwritable home directory is not a reason to
            // refuse to start. Environment-variable-configured connections still work, and the
            // secrets provider degrades to exactly the pre-fix behavior.
            return secretsPath;
        }

        return secretsPath;
    }
}
