using System.Text.Json;
using DotnetEfCoreMcp.Server.Connections;
using Microsoft.Extensions.Configuration;

namespace DotnetEfCoreMcp.Server.Tests.Connections;

/// <summary>Covers issue #121: the registry must not serve a startup snapshot forever. A connection
/// added to the configuration source while the server is running has to become visible without the
/// MCP client being restarted - either automatically (the source changed on disk) or through the
/// explicit <see cref="ConnectionRegistry.Reload"/> escape hatch.</summary>
public sealed class ConnectionRegistryRefreshTests : IDisposable
{
    private readonly string _secretsPath = Path.Combine(
        Path.GetTempPath(), $"efcore-mcp-connections-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_secretsPath))
        {
            File.Delete(_secretsPath);
        }
    }

    /// <summary>Mirrors how Program.cs adds the user-secrets store: a JSON file source without
    /// <c>reloadOnChange</c>, so the provider neither refreshes itself nor raises a reload token
    /// when an external <c>dotnet user-secrets set</c> rewrites the file.</summary>
    private IConfiguration BuildFileBackedConfiguration() =>
        new ConfigurationBuilder().AddJsonFile(_secretsPath, optional: true, reloadOnChange: false).Build();

    private void WriteConnections(params string[] names)
    {
        var connections = names.ToDictionary(name => name, name => BuildConnection(name, production: false));
        File.WriteAllText(_secretsPath, JsonSerializer.Serialize(new { Connections = connections }));
    }

    private void WriteConnectionWithConnectionString(string name, string connectionString)
    {
        var connection = BuildConnection(name, production: false);
        connection["ConnectionString"] = connectionString;
        File.WriteAllText(
            _secretsPath,
            JsonSerializer.Serialize(new { Connections = new Dictionary<string, object> { [name] = connection } }));
    }

    /// <summary>Writes a structurally valid but semantically malformed configuration: the connection
    /// is missing its required ConnectionString, which the registry rejects.</summary>
    private void WriteMalformedConnections() =>
        File.WriteAllText(_secretsPath, JsonSerializer.Serialize(new
        {
            Connections = new Dictionary<string, object>
            {
                ["Broken"] = new Dictionary<string, object> { ["Provider"] = "Sqlite" },
            },
        }));

    private void AppendConnection(string name, bool production)
    {
        var existing = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, JsonElement>>>(
            JsonDocument.Parse(File.ReadAllText(_secretsPath)).RootElement.GetProperty("Connections").GetRawText())!;

        var connections = existing.ToDictionary(pair => pair.Key, pair => (object)pair.Value);
        connections[name] = BuildConnection(name, production);

        File.WriteAllText(_secretsPath, JsonSerializer.Serialize(new { Connections = connections }));
    }

    private static Dictionary<string, object> BuildConnection(string name, bool production) => new()
    {
        ["Provider"] = "Sqlite",
        ["ConnectionString"] = $"Data Source={name}.db",
        ["Environment"] = production ? "Production" : "Development",
        ["AccessPolicy"] = new Dictionary<string, object>
        {
            ["AllowContexts"] = new[] { $"{name}.Context" },
        },
    };

    [Fact]
    public void ConnectionNames_WhenSourceFileGainsAConnection_ReflectsItWithoutReload()
    {
        WriteConnections("Alpha");
        var registry = new ConnectionRegistry(BuildFileBackedConfiguration());
        Assert.Equal(["Alpha"], registry.ConnectionNames.OrderBy(n => n, StringComparer.Ordinal));

        WriteConnections("Alpha", "Beta");

        Assert.Equal(["Alpha", "Beta"], registry.ConnectionNames.OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public void Get_WhenSourceFileGainsAConnection_ResolvesItWithoutReload()
    {
        WriteConnections("Alpha");
        var registry = new ConnectionRegistry(BuildFileBackedConfiguration());
        Assert.Throws<UnknownConnectionException>(() => registry.Get("Beta"));

        WriteConnections("Alpha", "Beta");

        Assert.Equal("Beta", registry.Get("Beta").Name);
    }

    [Fact]
    public void ListConnections_WhenSourceFileGainsAConnection_IncludesItWithoutReload()
    {
        WriteConnections("Alpha");
        var registry = new ConnectionRegistry(BuildFileBackedConfiguration());
        Assert.Single(registry.ListConnections());

        WriteConnections("Alpha", "Beta");

        Assert.Equal(2, registry.ListConnections().Count);
    }

    [Fact]
    public void Reload_ReportsAddedAndRemovedConnectionNames()
    {
        WriteConnections("Alpha", "Gone");
        var registry = new ConnectionRegistry(BuildFileBackedConfiguration());
        _ = registry.ConnectionNames;

        WriteConnections("Alpha", "Beta");
        var result = registry.Reload();

        Assert.Equal(["Beta"], result.AddedConnectionNames);
        Assert.Equal(["Gone"], result.RemovedConnectionNames);
        Assert.Null(result.ConfigurationError);
        Assert.Equal(["Alpha", "Beta"], result.Connections.Select(c => c.Name).OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public void Reload_PreservesTheActiveConnectionWhenItStillExists()
    {
        WriteConnections("Alpha", "Beta");
        var registry = new ConnectionRegistry(BuildFileBackedConfiguration());
        registry.SetActive("Beta");

        WriteConnections("Alpha", "Beta", "Gamma");
        registry.Reload();

        Assert.Equal("Beta", registry.ActiveConnectionName);
    }

    [Fact]
    public void Reload_WhenTheActiveConnectionDisappears_FallsBackToTheStartupSelectionRule()
    {
        WriteConnections("Alpha", "Beta");
        var registry = new ConnectionRegistry(BuildFileBackedConfiguration());
        registry.SetActive("Beta");

        WriteConnections("Alpha");
        registry.Reload();

        Assert.Equal("Alpha", registry.ActiveConnectionName);
    }

    [Fact]
    public void Reload_WhenReloadedConfigurationIsMalformed_ReportsTheConfigurationError()
    {
        WriteConnections("Alpha");
        var registry = new ConnectionRegistry(BuildFileBackedConfiguration());
        _ = registry.ConnectionNames;

        File.WriteAllText(_secretsPath, JsonSerializer.Serialize(new
        {
            Connections = new Dictionary<string, object>
            {
                ["Broken"] = new Dictionary<string, object> { ["Provider"] = "Sqlite" },
            },
        }));
        var result = registry.Reload();

        Assert.NotNull(result.ConfigurationError);
        Assert.Contains("ConnectionString", result.ConfigurationError!.Message, StringComparison.Ordinal);
        Assert.Empty(result.Connections);
        Assert.Throws<ConnectionRegistryConfigurationException>(() => registry.ListConnections());
    }

    [Fact]
    public void Reload_WhenAPreviouslyMalformedConfigurationIsFixed_RecoversWithoutRestart()
    {
        File.WriteAllText(_secretsPath, JsonSerializer.Serialize(new
        {
            Connections = new Dictionary<string, object>
            {
                ["Broken"] = new Dictionary<string, object> { ["Provider"] = "Sqlite" },
            },
        }));
        var registry = new ConnectionRegistry(BuildFileBackedConfiguration());
        Assert.NotNull(registry.ConfigurationError);

        WriteConnections("Alpha");
        var result = registry.Reload();

        Assert.Null(result.ConfigurationError);
        Assert.Equal(["Alpha"], registry.ConnectionNames);
        Assert.Equal("Alpha", registry.ActiveConnectionName);
    }

    [Fact]
    public void Reload_WithAnUnobservableConfigurationSource_StillReturnsTheCurrentView()
    {
        var registry = new ConnectionRegistry(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Connections:Alpha:Provider"] = "Sqlite",
                ["Connections:Alpha:ConnectionString"] = "Data Source=alpha.db",
                ["Connections:Alpha:AccessPolicy:AllowContexts:0"] = "Alpha.Context",
            })
            .Build());

        var result = registry.Reload();

        Assert.Empty(result.AddedConnectionNames);
        Assert.Empty(result.RemovedConnectionNames);
        Assert.Equal(["Alpha"], result.Connections.Select(c => c.Name));
    }

    [Fact]
    public void Reload_WhenAConnectionIsReclassifiedAsProduction_DropsItAsTheActiveConnection()
    {
        WriteConnections("Alpha", "Beta");
        var registry = new ConnectionRegistry(BuildFileBackedConfiguration());
        registry.SetActive("Beta");

        WriteConnections("Alpha");
        AppendConnection("Beta", production: true);
        registry.Reload();

        // The acknowledgment that allowed Beta to become active was given for a non-production
        // target, so it must not silently carry over to a production one.
        Assert.Equal("Alpha", registry.ActiveConnectionName);
    }

    [Fact]
    public void SchemaCacheKey_ChangesOnlyWhenAConnectionIsRepointedAtAnotherDatabase()
    {
        WriteConnections("Alpha");
        var registry = new ConnectionRegistry(BuildFileBackedConfiguration());
        var original = registry.Get("Alpha").SchemaCacheKey;

        // An unrelated edit (a second connection appearing) must not invalidate Alpha's cached
        // schema, but repointing Alpha itself must.
        WriteConnections("Alpha", "Beta");
        Assert.Equal(original, registry.Get("Alpha").SchemaCacheKey);

        File.WriteAllText(_secretsPath, JsonSerializer.Serialize(new
        {
            Connections = new Dictionary<string, object>
            {
                ["Alpha"] = new Dictionary<string, object>
                {
                    ["Provider"] = "Sqlite",
                    ["ConnectionString"] = "Data Source=somewhere-else.db",
                    ["Environment"] = "Development",
                    ["AccessPolicy"] = new Dictionary<string, object> { ["AllowContexts"] = new[] { "Alpha.Context" } },
                },
            },
        }));

        Assert.NotEqual(original, registry.Get("Alpha").SchemaCacheKey);
    }

    [Fact]
    public void ConnectionNames_WhenTheSourceFileIsCreatedAfterStartup_PicksItUpWithoutReload()
    {
        // The fresh-install shape: nothing configured yet when the server starts, and the user adds
        // their first connection afterwards. The absent file must not blind the registry forever.
        Assert.False(File.Exists(_secretsPath));
        var registry = new ConnectionRegistry(BuildFileBackedConfiguration());
        Assert.Empty(registry.ConnectionNames);

        WriteConnections("Alpha");

        Assert.Equal(["Alpha"], registry.ConnectionNames);
    }

    [Fact]
    public void Reload_WhenTheSourceFileIsCreatedAfterStartup_ReportsItAsAdded()
    {
        Assert.False(File.Exists(_secretsPath));
        var registry = new ConnectionRegistry(BuildFileBackedConfiguration());
        _ = registry.ConnectionNames;

        WriteConnections("Alpha");
        var result = registry.Reload();

        Assert.Equal(["Alpha"], result.AddedConnectionNames);
    }

    [Fact]
    public void ConnectionNames_WhenTheSourceFileIsDeletedAfterStartup_DropsTheConnections()
    {
        WriteConnections("Alpha");
        var registry = new ConnectionRegistry(BuildFileBackedConfiguration());
        Assert.Single(registry.ConnectionNames);

        File.Delete(_secretsPath);

        Assert.Empty(registry.ConnectionNames);
    }

    [Fact]
    public void SchemaCacheKey_DetectsARepointThatHappenedWhileTheConfigurationWasMalformed()
    {
        // Review finding on #123: a malformed intermediate state must not erase the connection-string
        // history that revision tracking depends on. Otherwise the fix below resolves back to
        // revision 0, reproducing the pre-edit cache key and serving the old database's schema.
        WriteConnectionWithConnectionString("Alpha", "Data Source=original.db");
        var registry = new ConnectionRegistry(BuildFileBackedConfiguration());
        var original = registry.Get("Alpha").SchemaCacheKey;

        WriteMalformedConnections();
        Assert.Throws<ConnectionRegistryConfigurationException>(() => registry.Get("Alpha"));

        WriteConnectionWithConnectionString("Alpha", "Data Source=somewhere-else.db");

        Assert.NotEqual(original, registry.Get("Alpha").SchemaCacheKey);
    }

    [Fact]
    public void ActiveConnection_WhileTheConfigurationIsMalformed_IsNotServedFromTheStaleRegistry()
    {
        WriteConnections("Alpha");
        var registry = new ConnectionRegistry(BuildFileBackedConfiguration());
        Assert.Equal("Alpha", registry.ActiveConnectionName);

        WriteMalformedConnections();

        // Fail closed: a broken configuration must not keep serving the previously loaded entries.
        Assert.Throws<ConnectionRegistryConfigurationException>(() => registry.ActiveConnectionName);
        Assert.Throws<ConnectionRegistryConfigurationException>(() => registry.ListConnections());
    }

    [Fact]
    public void ConnectionNames_DetectASameLengthRewriteWithinOneTimestampTick()
    {
        // A connection-string edit often preserves the file's length, and the write can land inside a
        // single timestamp tick, so a length+timestamp fingerprint would miss it entirely.
        WriteConnectionWithConnectionString("Alpha", "Data Source=aaaa.db");
        var registry = new ConnectionRegistry(BuildFileBackedConfiguration());
        var original = registry.Get("Alpha").SchemaCacheKey;
        var timestamp = File.GetLastWriteTimeUtc(_secretsPath);

        WriteConnectionWithConnectionString("Alpha", "Data Source=bbbb.db");
        File.SetLastWriteTimeUtc(_secretsPath, timestamp);

        Assert.Equal("Data Source=bbbb.db", registry.Get("Alpha").ConnectionString);
        Assert.NotEqual(original, registry.Get("Alpha").SchemaCacheKey);
    }

    [Fact]
    public void Reload_WhenTheSourceIsNotValidJson_ReportsTheUnderlyingCause()
    {
        WriteConnections("Alpha");
        var registry = new ConnectionRegistry(BuildFileBackedConfiguration());
        _ = registry.ConnectionNames;

        File.WriteAllText(_secretsPath, "{ this is not valid json");
        var result = registry.Reload();

        // The parser names the offending file and position; that detail is the only actionable part.
        Assert.NotNull(result.ConfigurationError);
        Assert.Contains("Cause:", result.ConfigurationError!.Message, StringComparison.Ordinal);
        Assert.Contains("reload", result.ConfigurationError.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Reload_AfterAnInvalidJsonSourceIsRepaired_RecoversWithoutRestart()
    {
        WriteConnections("Alpha");
        var registry = new ConnectionRegistry(BuildFileBackedConfiguration());
        _ = registry.ConnectionNames;
        File.WriteAllText(_secretsPath, "{ this is not valid json");
        Assert.NotNull(registry.Reload().ConfigurationError);

        WriteConnections("Alpha", "Beta");
        var result = registry.Reload();

        Assert.Null(result.ConfigurationError);
        Assert.Equal(["Alpha", "Beta"], registry.ConnectionNames.OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public void UnknownConnectionMessage_PointsAtReloadConnectionsRatherThanAClientRestart()
    {
        WriteConnections("Alpha");
        var registry = new ConnectionRegistry(BuildFileBackedConfiguration());

        var exception = Assert.Throws<UnknownConnectionException>(() => registry.Get("Missing"));

        Assert.Contains("reload_connections", exception.Message, StringComparison.Ordinal);
    }
}
