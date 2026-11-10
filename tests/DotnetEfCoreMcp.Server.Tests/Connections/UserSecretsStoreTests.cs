using System.Reflection;
using System.Reflection.Emit;
using System.Text.Json;
using DotnetEfCoreMcp.Server.Connections;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.UserSecrets;

namespace DotnetEfCoreMcp.Server.Tests.Connections;

/// <summary>Covers the user-secrets rooting trap behind issue #121's "cannot be refreshed" half.
///
/// <c>AddUserSecrets</c> resolves the secrets file's directory up front; when that directory does
/// not exist it silently roots its <c>PhysicalFileProvider</c> at the *application base directory*
/// instead. The provider is then permanently unable to see the real <c>secrets.json</c> - creating
/// the file later changes nothing, and not even an explicit <c>IConfigurationRoot.Reload()</c>
/// recovers it. That is the fresh-install case (a user configuring their first connection after
/// starting the server), and it would have made <c>reload_connections</c> useless exactly when it is
/// needed most.</summary>
public sealed class UserSecretsStoreTests : IDisposable
{
    private readonly string _secretsId = $"efcore-mcp-test-{Guid.NewGuid():N}";

    private string SecretsDirectory => Path.GetDirectoryName(PathHelper.GetSecretsPathFromSecretsId(_secretsId))!;

    public void Dispose()
    {
        if (Directory.Exists(SecretsDirectory))
        {
            Directory.Delete(SecretsDirectory, recursive: true);
        }
    }

    [Fact]
    public void EnsureDirectoryExists_CreatesTheSecretsDirectory()
    {
        Assert.False(Directory.Exists(SecretsDirectory));

        var path = UserSecretsStore.EnsureDirectoryExists(BuildAssemblyWithSecretsId());

        Assert.NotNull(path);
        Assert.True(Directory.Exists(SecretsDirectory));
    }

    [Fact]
    public void EnsureDirectoryExists_IsIdempotent()
    {
        var assembly = BuildAssemblyWithSecretsId();

        var first = UserSecretsStore.EnsureDirectoryExists(assembly);
        var second = UserSecretsStore.EnsureDirectoryExists(assembly);

        Assert.Equal(first, second);
        Assert.True(Directory.Exists(SecretsDirectory));
    }

    [Fact]
    public void EnsureDirectoryExists_WithoutAUserSecretsId_ReturnsNullRatherThanThrowing()
    {
        Assert.Null(UserSecretsStore.EnsureDirectoryExists(typeof(object).Assembly));
    }

    [Fact]
    public void WithoutEnsureDirectoryExists_AddUserSecretsCanNeverSeeASecretsFileCreatedLater()
    {
        // Documents the trap itself, so a future change that drops the EnsureDirectoryExists call
        // fails loudly here instead of silently reintroducing the unrefreshable registry.
        Assert.False(Directory.Exists(SecretsDirectory));
        var configuration = new ConfigurationBuilder()
            .AddUserSecrets(BuildAssemblyWithSecretsId(), optional: true)
            .Build();

        WriteSecretsFile();
        configuration.Reload();

        Assert.Empty(configuration.GetSection("Connections").GetChildren());
    }

    [Fact]
    public void WithEnsureDirectoryExists_ASecretsFileCreatedLaterBecomesVisible()
    {
        var assembly = BuildAssemblyWithSecretsId();
        UserSecretsStore.EnsureDirectoryExists(assembly);
        var configuration = new ConfigurationBuilder()
            .AddUserSecrets(assembly, optional: true)
            .Build();
        Assert.Empty(configuration.GetSection("Connections").GetChildren());

        WriteSecretsFile();

        var registry = new ConnectionRegistry(configuration);
        Assert.Equal(["Alpha"], registry.ConnectionNames);
    }

    private void WriteSecretsFile()
    {
        Directory.CreateDirectory(SecretsDirectory);
        File.WriteAllText(
            PathHelper.GetSecretsPathFromSecretsId(_secretsId),
            JsonSerializer.Serialize(new
            {
                Connections = new Dictionary<string, object>
                {
                    ["Alpha"] = new Dictionary<string, object>
                    {
                        ["Provider"] = "Sqlite",
                        ["ConnectionString"] = "Data Source=alpha.db",
                        ["Environment"] = "Development",
                        ["AccessPolicy"] = new Dictionary<string, object>
                        {
                            ["AllowContexts"] = new[] { "Alpha.Context" },
                        },
                    },
                },
            }));
    }

    /// <summary>Builds a throwaway in-memory assembly carrying a unique
    /// <see cref="UserSecretsIdAttribute"/>, so each test gets its own secrets directory instead of
    /// touching the real one this repository uses.</summary>
    private Assembly BuildAssemblyWithSecretsId()
    {
        var assemblyName = new AssemblyName($"UserSecretsStoreTests_{Guid.NewGuid():N}");
        var builder = AssemblyBuilder.DefineDynamicAssembly(assemblyName, AssemblyBuilderAccess.Run);
        builder.SetCustomAttribute(new CustomAttributeBuilder(
            typeof(UserSecretsIdAttribute).GetConstructor([typeof(string)])!,
            [_secretsId]));
        return builder;
    }
}
