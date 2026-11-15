using DotnetEfCoreMcp.Server.Tests.TestSupport;

namespace DotnetEfCoreMcp.Server.Tests.Cli;

[Collection(ApplicationFactoryEnvironmentCollection.Name)]
public sealed class CliSessionFlowTests : IDisposable
{
    private readonly CliTestHost _host = new();

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task LoadAssembly_ThenListContexts_ReusesTheAssemblyAcrossSeparateRuns()
    {
        var load = await _host.RunAsync("load_assembly", "--assembly-path", FixturePaths.SampleAppDllPath, "--output", "json");
        Assert.Equal(0, load.ExitCode);

        var contexts = await _host.RunAsync("list_contexts");

        Assert.Equal(0, contexts.ExitCode);
        Assert.Contains("SampleAppDbContext", contexts.Output, StringComparison.Ordinal);
        Assert.Contains(Path.GetFileName(FixturePaths.SampleAppDllPath), contexts.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadAssembly_ThenListContexts_ThenRunQuery_ReplaysTheMcpFlow()
    {
        await _host.RunAsync("load_assembly", "--assembly-path", FixturePaths.SampleAppDllPath);

        var contexts = await _host.RunAsync("list_contexts");
        var query = await _host.RunAsync("run_query", "--context-name", "SampleAppDbContext", "--query", "Customers.OrderBy(c => c.Id).Select(c => c.Name)", "--connection", CliTestHost.ConnectionName);

        Assert.Equal(0, contexts.ExitCode);
        Assert.Equal(0, query.ExitCode);
        Assert.Contains("Customer 1", query.Output, StringComparison.Ordinal);
        Assert.Contains("Customer 3", query.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SwapConnection_IsRememberedSoLaterRunsNeedNoConnectionFlag()
    {
        await _host.RunAsync("load_assembly", "--assembly-path", FixturePaths.SampleAppDllPath);
        var swap = await _host.RunAsync("swap_connection", "--connection-name", CliTestHost.ConnectionName);
        Assert.Equal(0, swap.ExitCode);

        var query = await _host.RunAsync("run_query", "--context-name", "SampleAppDbContext", "--query", "Customers.Count()");

        Assert.Equal(0, query.ExitCode);
        Assert.Contains("3", query.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NamedTargetAndSelectTarget_AreRememberedAcrossRuns()
    {
        await _host.RunAsync("load_assembly", "--assembly-path", FixturePaths.SampleAppDllPath, "--target-name", "sample");
        var select = await _host.RunAsync("select_target", "--target-name", "sample");
        Assert.Equal(0, select.ExitCode);

        var targets = await _host.RunAsync("list_loaded_assemblies");

        Assert.Equal(0, targets.ExitCode);
        Assert.Contains("sample", targets.Output, StringComparison.Ordinal);
        Assert.Contains("true", targets.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Session_StoresOnlyNamesPathsAndTheCursorKey_NeverTheConnectionString()
    {
        await _host.RunAsync("load_assembly", "--assembly-path", FixturePaths.SampleAppDllPath);
        await _host.RunAsync("swap_connection", "--connection-name", CliTestHost.ConnectionName);
        await _host.RunAsync("run_query", "--context-name", "SampleAppDbContext", "--query", "Customers.OrderBy(c => c.Id).Take(1)", "--pagination", """{"mode":"cursor"}""");

        var shown = await _host.RunAsync("session", "show");

        Assert.Equal(0, shown.ExitCode);
        Assert.Contains("targets", shown.Output, StringComparison.Ordinal);
        Assert.Contains("activeConnection", shown.Output, StringComparison.Ordinal);
        Assert.Contains("cursorKey", shown.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(_host.Database.ConnectionString, shown.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("Data Source", shown.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionClear_ForgetsTheRememberedTarget()
    {
        await _host.RunAsync("load_assembly", "--assembly-path", FixturePaths.SampleAppDllPath);
        await _host.RunAsync("session", "clear");

        var contexts = await _host.RunAsync("list_contexts");

        Assert.DoesNotContain("SampleAppDbContext", contexts.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExplicitAssemblyFlag_WinsOverAnAssemblyRememberedInTheSession()
    {
        await _host.RunAsync("load_assembly", "--assembly-path", FixturePaths.NoContextAppDllPath);

        var contexts = await _host.RunAsync("list_contexts", "--assembly", FixturePaths.SampleAppDllPath);

        Assert.Equal(0, contexts.ExitCode);
        Assert.Contains("SampleAppDbContext", contexts.Output, StringComparison.Ordinal);
    }
}
