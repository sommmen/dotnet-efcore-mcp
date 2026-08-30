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
    public void Constructor_MissingProvider_ThrowsConfigurationException()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Connections:Bad:ConnectionString"] = "Data Source=test.db",
        });

        Assert.Throws<ConnectionRegistryConfigurationException>(() => new ConnectionRegistry(configuration));
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
    public void Entry_ToString_NeverIncludesRawConnectionString()
    {
        var entry = new ConnectionRegistryEntry
        {
            Name = "Secret",
            Provider = DatabaseProvider.Sqlite,
            ConnectionString = "Data Source=super-secret-path.db;Password=hunter2",
        };

        var rendered = entry.ToString();

        Assert.DoesNotContain("hunter2", rendered);
        Assert.DoesNotContain("super-secret-path.db", rendered);
        Assert.Contains("REDACTED", rendered);
    }
}
