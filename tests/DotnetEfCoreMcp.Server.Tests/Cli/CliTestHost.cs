using DotnetEfCoreMcp.Server.AssemblyLoading;
using DotnetEfCoreMcp.Server.Cli;
using DotnetEfCoreMcp.Server.Connections;
using DotnetEfCoreMcp.Server.DbContextDiscovery;
using DotnetEfCoreMcp.Server.Tests.TestSupport;

namespace DotnetEfCoreMcp.Server.Tests.Cli;

public sealed record CliResult(int ExitCode, string Output, string Error);

/// <summary>Runs <c>dotnet-efcore-mcp cli ...</c> in-process with an isolated session directory, a
/// seeded SQLite database and the SampleApp fixture, so each test is independent.</summary>
public sealed class CliTestHost : IDisposable
{
    public const string ConnectionName = "CliTest";

    private readonly string _sessionDirectory = Path.Combine(AppContext.BaseDirectory, "TestData", "cli-sessions", Guid.NewGuid().ToString("N"));

    public CliTestHost(int customerCount = 3)
    {
        Database = new SqliteTestDatabase();
        Seed(Database, customerCount);
    }

    public SqliteTestDatabase Database { get; }

    public string SessionId { get; } = "test-" + Guid.NewGuid().ToString("N");

    /// <summary>Global flags that point the CLI at the seeded SQLite database for the SampleApp fixture.</summary>
    public static string[] ConnectionConfig(SqliteTestDatabase database) =>
    [
        "--config", $"Connections:{ConnectionName}:ConnectionString={database.ConnectionString}",
        "--config", $"Connections:{ConnectionName}:Provider=Sqlite",
        "--config", $"Connections:{ConnectionName}:Environment=Development",
        "--config", $"Connections:{ConnectionName}:AccessPolicy:AllowContexts:0=SampleApp.SampleAppDbContext",
        "--config", $"QueryExecution:OutOfProcessHostPath={FixturePaths.QueryHostDllPath}",
        "--session", "{session}",
    ];

    public Task<CliResult> RunAsync(params string[] args) => RunAsync(CancellationToken.None, args);

    public async Task<CliResult> RunAsync(CancellationToken cancellationToken, params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var globalFlags = ConnectionConfig(Database).Select(flag => flag == "{session}" ? SessionId : flag);
        var exitCode = await EfCoreSurface.RunAsync(
            ["cli", .. globalFlags, .. args],
            new EfCoreSurfaceOptions
            {
                Output = output,
                Error = error,
                Input = TextReader.Null,
                SessionStateDirectory = _sessionDirectory,
                HandleCancelKeyPress = false,
            },
            cancellationToken);
        return new CliResult(exitCode, output.ToString(), error.ToString());
    }

    public void Dispose()
    {
        Database.Dispose();
        try
        {
            if (Directory.Exists(_sessionDirectory))
            {
                Directory.Delete(_sessionDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    private static void Seed(SqliteTestDatabase database, int customerCount)
    {
        var handle = new AssemblyLoaderService().Load(FixturePaths.SampleAppDllPath);
        try
        {
            var contextType = DbContextScanner.FindDbContextTypes(handle.Assembly).Descriptors.Single(d => d.Name == "SampleAppDbContext").ClrType;
            using var context = DbContextActivator.CreateInstance(contextType, database.ToRegistryEntry(ConnectionName), DatabaseProvider.Sqlite);
            context.Database.EnsureCreated();
            var customerType = EntitySeeding.GetEntityClrType(context, "Customer");
            for (var index = 1; index <= customerCount; index++)
            {
                context.Add(EntitySeeding.CreateEntity(customerType, new Dictionary<string, object?> { ["Name"] = $"Customer {index}", ["Age"] = 20 + index }));
            }

            context.SaveChanges();
        }
        finally
        {
            handle.Unload();
        }
    }
}
