using DotnetEfCoreMcp.Server.Connections;
using Microsoft.Extensions.Configuration;

namespace DotnetEfCoreMcp.Server.Tests.Connections;

public sealed class ConnectionRegistryTests
{
    private static IConfiguration BuildConfiguration(IDictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    [Fact]
    public void Get_KnownConnection_ReturnsEntryWithParsedFields()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Connections:MyApp.Context:Provider"] = "Sqlite",
            ["Connections:MyApp.Context:ConnectionString"] = "Data Source=test.db",
            ["Connections:MyApp.Context:AccessMode"] = "ReadOnly",
            ["Connections:MyApp.Context:CommandTimeoutSeconds"] = "45",
            ["Connections:MyApp.Context:AccessPolicy:AllowContexts:0"] = "MyApp.Context",
        });
        var registry = new ConnectionRegistry(configuration);

        var entry = registry.Get("MyApp.Context");

        Assert.Equal("MyApp.Context", entry.Name);
        Assert.Equal(DatabaseProvider.Sqlite, entry.Provider);
        Assert.Equal("Data Source=test.db", entry.ConnectionString);
        Assert.Equal(ConnectionAccessMode.ReadOnly, entry.AccessMode);
        Assert.Equal(45, entry.CommandTimeoutSeconds);
    }

    [Fact]
    public void Get_UnknownConnection_ThrowsUnknownConnectionExceptionListingKnownNames()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Connections:Known:Provider"] = "Sqlite",
            ["Connections:Known:ConnectionString"] = "Data Source=test.db",
            ["Connections:Known:AccessPolicy:AllowContexts:0"] = "Known.Context",
        });
        var registry = new ConnectionRegistry(configuration);

        var ex = Assert.Throws<UnknownConnectionException>(() => registry.Get("DoesNotExist"));

        Assert.Contains("DoesNotExist", ex.Message);
        Assert.Contains("Known", ex.Message);
    }

    [Fact]
    public void TryGet_UnknownConnection_ReturnsFalseRatherThanThrowing()
    {
        var registry = new ConnectionRegistry(BuildConfiguration(new Dictionary<string, string?>()));

        var found = registry.TryGet("Anything", out _);

        Assert.False(found);
    }

    [Fact]
    public void Constructor_MissingProvider_LeavesProviderNullForInference()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Connections:Inferred:ConnectionString"] = "Data Source=test.db",
            ["Connections:Inferred:AccessPolicy:AllowContexts:0"] = "Inferred.Context",
        });

        var registry = new ConnectionRegistry(configuration);
        var entry = registry.Get("Inferred");

        Assert.Null(entry.Provider);
        Assert.Equal("Data Source=test.db", entry.ConnectionString);
    }

    [Fact]
    public void Constructor_MissingConnectionString_ThrowsConfigurationException()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Connections:Bad:Provider"] = "Sqlite",
        });

        Assert.Throws<ConnectionRegistryConfigurationException>(() => new ConnectionRegistry(configuration));
    }

    [Fact]
    public void Constructor_UnsupportedProvider_ThrowsConfigurationException()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Connections:Bad:Provider"] = "MongoDb",
            ["Connections:Bad:ConnectionString"] = "whatever",
        });

        Assert.Throws<ConnectionRegistryConfigurationException>(() => new ConnectionRegistry(configuration));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("not-a-number")]
    public void Constructor_InvalidCommandTimeoutSeconds_ThrowsConfigurationException(string commandTimeoutSeconds)
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Connections:Bad:Provider"] = "Sqlite",
            ["Connections:Bad:ConnectionString"] = "Data Source=test.db",
            ["Connections:Bad:CommandTimeoutSeconds"] = commandTimeoutSeconds,
        });

        Assert.Throws<ConnectionRegistryConfigurationException>(() => new ConnectionRegistry(configuration));
    }

    [Fact]
    public void Constructor_InvalidAccessMode_ThrowsConfigurationException()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Connections:Bad:Provider"] = "Sqlite",
            ["Connections:Bad:ConnectionString"] = "Data Source=test.db",
            ["Connections:Bad:AccessMode"] = "SuperAdmin",
        });

        Assert.Throws<ConnectionRegistryConfigurationException>(() => new ConnectionRegistry(configuration));
    }

    [Fact]
    public void Get_ConnectionWithEnvironment_ParsesEnvironment()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Connections:Staging:Provider"] = "Sqlite",
            ["Connections:Staging:ConnectionString"] = "Data Source=staging.db",
            ["Connections:Staging:Environment"] = "staging",
            ["Connections:Staging:AccessPolicy:AllowContexts:0"] = "Staging.Context",
        });

        var entry = new ConnectionRegistry(configuration).Get("Staging");

        Assert.Equal(EnvironmentType.Staging, entry.Environment);
        Assert.False(entry.IsProduction);
    }

    [Fact]
    public void Constructor_InvalidEnvironment_ThrowsConfigurationException()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Connections:Bad:Provider"] = "Sqlite",
            ["Connections:Bad:ConnectionString"] = "Data Source=test.db",
            ["Connections:Bad:Environment"] = "DisasterRecovery",
        });

        var exception = Assert.Throws<ConnectionRegistryConfigurationException>(() => new ConnectionRegistry(configuration));

        Assert.Contains("Environment", exception.Message);
        Assert.Contains("DisasterRecovery", exception.Message);
    }

    [Fact]
    public void Constructor_ProductionConnection_ForcesReadOnlyAccessMode()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Connections:Production:Provider"] = "Sqlite",
            ["Connections:Production:ConnectionString"] = "Data Source=production.db",
            ["Connections:Production:Environment"] = "Production",
            ["Connections:Production:AccessMode"] = "ReadWrite",
            ["Connections:Production:AccessPolicy:AllowContexts:0"] = "Production.Context",
        });

        var entry = new ConnectionRegistry(configuration).Get("Production");

        Assert.True(entry.IsProduction);
        Assert.Equal(ConnectionAccessMode.ReadOnly, entry.AccessMode);
    }

    [Fact]
    public void Constructor_DefaultsActiveConnectionToFirstNonProductionConnection()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Connections:Production:Provider"] = "Sqlite",
            ["Connections:Production:ConnectionString"] = "Data Source=production.db",
            ["Connections:Production:Environment"] = "Production",
            ["Connections:Production:AccessPolicy:AllowContexts:0"] = "Production.Context",
            ["Connections:Development:Provider"] = "Sqlite",
            ["Connections:Development:ConnectionString"] = "Data Source=development.db",
            ["Connections:Development:Environment"] = "Development",
            ["Connections:Development:AccessPolicy:AllowContexts:0"] = "Development.Context",
        });

        var registry = new ConnectionRegistry(configuration);

        Assert.Equal("Development", registry.ActiveConnectionName);
        Assert.Same(registry.Get("Development"), registry.ActiveConnection);
    }

    [Fact]
    public void Constructor_WithOnlyProductionConnections_HasNoActiveConnection()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Connections:Production:Provider"] = "Sqlite",
            ["Connections:Production:ConnectionString"] = "Data Source=production.db",
            ["Connections:Production:Environment"] = "Production",
            ["Connections:Production:AccessPolicy:AllowContexts:0"] = "Production.Context",
        });

        var registry = new ConnectionRegistry(configuration);

        Assert.Null(registry.ActiveConnectionName);
        Assert.Null(registry.ActiveConnection);
    }

    [Fact]
    public void SetActive_NonProductionConnection_ChangesActiveConnection()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Connections:Development:Provider"] = "Sqlite",
            ["Connections:Development:ConnectionString"] = "Data Source=development.db",
            ["Connections:Development:AccessPolicy:AllowContexts:0"] = "Development.Context",
            ["Connections:Staging:Provider"] = "Sqlite",
            ["Connections:Staging:ConnectionString"] = "Data Source=staging.db",
            ["Connections:Staging:Environment"] = "Staging",
            ["Connections:Staging:AccessPolicy:AllowContexts:0"] = "Staging.Context",
        });
        var registry = new ConnectionRegistry(configuration);

        registry.SetActive("Staging");

        Assert.Equal("Staging", registry.ActiveConnectionName);
        Assert.Same(registry.Get("Staging"), registry.ActiveConnection);
    }

    [Fact]
    public void SetActive_ProductionConnectionWithoutAcknowledgment_ThrowsAndPreservesActiveConnection()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Connections:Development:Provider"] = "Sqlite",
            ["Connections:Development:ConnectionString"] = "Data Source=development.db",
            ["Connections:Development:AccessPolicy:AllowContexts:0"] = "Development.Context",
            ["Connections:Production:Provider"] = "Sqlite",
            ["Connections:Production:ConnectionString"] = "Data Source=production.db",
            ["Connections:Production:Environment"] = "Production",
            ["Connections:Production:AccessPolicy:AllowContexts:0"] = "Production.Context",
        });
        var registry = new ConnectionRegistry(configuration);
        var originalActive = registry.ActiveConnectionName;

        var exception = Assert.Throws<ProductionProtectedException>(() => registry.SetActive("Production"));

        Assert.Equal("Production", exception.ConnectionName);
        Assert.Equal(originalActive, registry.ActiveConnectionName);
    }

    [Fact]
    public void SetActive_ProductionConnectionWithAcknowledgment_ChangesActiveConnection()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Connections:Development:Provider"] = "Sqlite",
            ["Connections:Development:ConnectionString"] = "Data Source=development.db",
            ["Connections:Development:AccessPolicy:AllowContexts:0"] = "Development.Context",
            ["Connections:Production:Provider"] = "Sqlite",
            ["Connections:Production:ConnectionString"] = "Data Source=production.db",
            ["Connections:Production:Environment"] = "Production",
            ["Connections:Production:AccessPolicy:AllowContexts:0"] = "Production.Context",
        });
        var registry = new ConnectionRegistry(configuration);

        registry.SetActive("Production", allowProduction: true);

        Assert.Equal("Production", registry.ActiveConnectionName);
        Assert.True(registry.ActiveConnection!.IsProduction);
        Assert.Equal(ConnectionAccessMode.ReadOnly, registry.ActiveConnection.AccessMode);
    }

    [Fact]
    public void ListConnections_ReturnsRedactedMetadataAndMarksActiveConnection()
    {
        const string secret = "Data Source=super-secret-production.db;Password=hunter2";
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Connections:Development:Provider"] = "Sqlite",
            ["Connections:Development:ConnectionString"] = "Data Source=development.db",
            ["Connections:Development:Environment"] = "Development",
            ["Connections:Development:AccessPolicy:AllowContexts:0"] = "Development.Context",
            ["Connections:Production:Provider"] = "Sqlite",
            ["Connections:Production:ConnectionString"] = secret,
            ["Connections:Production:Environment"] = "Production",
            ["Connections:Production:AccessPolicy:AllowContexts:0"] = "Production.Context",
        });
        var registry = new ConnectionRegistry(configuration);

        var connections = registry.ListConnections();

        Assert.Collection(connections,
            development =>
            {
                Assert.Equal("Development", development.Name);
                Assert.True(development.IsActive);
                Assert.False(development.IsProduction);
            },
            production =>
            {
                Assert.Equal("Production", production.Name);
                Assert.False(production.IsActive);
                Assert.True(production.IsProduction);
                Assert.Equal(ConnectionAccessMode.ReadOnly, production.AccessMode);
            });
        Assert.DoesNotContain(secret, connections.ToString());
        Assert.DoesNotContain("hunter2", connections.ToString());
    }

    [Fact]
    public void Entry_ToString_NeverIncludesRawConnectionString()
    {
        var entry = new ConnectionRegistryEntry
        {
            Name = "Secret",
            Provider = DatabaseProvider.Sqlite,
            ConnectionString = "Data Source=super-secret-path.db;Password=hunter2",
        AccessPolicy = new ConnectionAccessPolicy
        {
            AllowContexts = [],
            DenyContexts = [],
            AllowEntities = [],
            DenyEntities = [],
        },
        };

        var rendered = entry.ToString();

        Assert.DoesNotContain("hunter2", rendered);
        Assert.DoesNotContain("super-secret-path.db", rendered);
        Assert.Contains("REDACTED", rendered);
    }
}
