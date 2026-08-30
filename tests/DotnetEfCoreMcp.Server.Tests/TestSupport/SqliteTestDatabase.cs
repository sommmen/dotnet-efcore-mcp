using DotnetEfCoreMcp.Server.Connections;

namespace DotnetEfCoreMcp.Server.Tests.TestSupport;

/// <summary>A throwaway SQLite database file under the test project's own output directory (not
/// the OS temp folder), deleted on <see cref="Dispose"/>. File-based (rather than the `:memory:`
/// keyword) so that separate <see cref="Microsoft.EntityFrameworkCore.DbContext"/> instances /
/// connections opened during a test can all see the same data, matching how a real target
/// project's database behaves.</summary>
public sealed class SqliteTestDatabase : IDisposable
{
    public SqliteTestDatabase()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "TestData");
        Directory.CreateDirectory(directory);
        Path_ = Path.Combine(directory, $"efcoremcp_test_{Guid.NewGuid():N}.db");
        ConnectionString = $"Data Source={Path_}";
    }

    private string Path_ { get; }

    public string ConnectionString { get; }

    public ConnectionRegistryEntry ToRegistryEntry(string name = "TestConnection", int commandTimeoutSeconds = 30) => new()
    {
        Name = name,
        Provider = DatabaseProvider.Sqlite,
        ConnectionString = ConnectionString,
        AccessMode = ConnectionAccessMode.ReadOnly,
        CommandTimeoutSeconds = commandTimeoutSeconds,
    };

    public void Dispose()
    {
        // SQLite can keep the file briefly locked after the last connection using it is disposed
        // (pooling); best-effort cleanup rather than failing the test run over a leftover temp
        // file.
        try
        {
            if (File.Exists(Path_))
            {
                File.Delete(Path_);
            }
        }
        catch (IOException)
        {
        }
    }
}
