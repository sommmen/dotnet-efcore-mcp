using System.Diagnostics;
using System.Text.Json;
using DotnetEfCoreMcp.Server.Cli;
using DotnetEfCoreMcp.Server.Tests.TestSupport;

namespace DotnetEfCoreMcp.Server.Tests.Cli;

/// <summary>Runs the built server DLL as a real process, the way an agent's shell tool would.</summary>
[Collection(ApplicationFactoryEnvironmentCollection.Name)]
public sealed class CliProcessSmokeTests : IDisposable
{
    private readonly CliTestHost _host = new();
    private readonly string _stateRoot = Path.Combine(AppContext.BaseDirectory, "TestData", "cli-smoke-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        _host.Dispose();
        try
        {
            if (Directory.Exists(_stateRoot))
            {
                Directory.Delete(_stateRoot, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task ListContexts_RunsFromTheBuiltDll()
    {
        var (exitCode, stdout, _) = await RunAsync("list_contexts", "--assembly", FixturePaths.SampleAppDllPath, "--config", "ToolOutput:Format=json");

        Assert.Equal(0, exitCode);
        using var document = JsonDocument.Parse(stdout);
        Assert.Contains(document.RootElement.GetProperty("contexts").EnumerateArray(), context => context.GetProperty("name").GetString() == "SampleAppDbContext");
    }

    [Fact]
    public async Task RunQuery_FindsTheBundledQueryHostAndKeepsStdoutClean()
    {
        var (exitCode, stdout, _) = await RunAsync(
            "run_query", "--assembly", FixturePaths.SampleAppDllPath, "--connection", CliTestHost.ConnectionName,
            "--context-name", "SampleAppDbContext", "--query", "Customers.OrderBy(c => c.Id).Select(c => c.Name)", "--config", "ToolOutput:Format=json");

        Assert.Equal(0, exitCode);
        using var document = JsonDocument.Parse(stdout);
        Assert.Equal(3, document.RootElement.GetProperty("RowCount").GetInt32());
    }

    [Fact]
    public async Task LoadAssemblyThenRunQuery_ReplaysTheFlowAcrossProcesses()
    {
        var load = await RunAsync("load_assembly", "--assembly-path", FixturePaths.SampleAppDllPath);
        Assert.Equal(0, load.ExitCode);
        Assert.True(File.Exists(Path.Combine(_stateRoot, "dotnet-efcore-mcp", "sessions", _host.SessionId + ".json")),
            "The session must be written under the isolated state directory, never the real per-user one.");

        var query = await RunAsync(
            "run_query", "--connection", CliTestHost.ConnectionName, "--context-name", "SampleAppDbContext",
            "--query", "Customers.Count()", "--config", "ToolOutput:Format=json");

        Assert.Equal(0, query.ExitCode);
        Assert.Contains("3", query.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnloadableAssembly_ExitsWithOne()
    {
        var (exitCode, stdout, stderr) = await RunAsync("list_contexts", "--assembly", Path.Combine(_stateRoot, "missing.dll"));

        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.Contains("--assembly", stderr, StringComparison.Ordinal);
    }

    private async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(params string[] args)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = Path.GetDirectoryName(FixturePaths.ServerDllPath)!,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(FixturePaths.ServerDllPath);
        startInfo.ArgumentList.Add("cli");
        foreach (var flag in CliTestHost.ConnectionConfig(_host.Database).Select(flag => flag == "{session}" ? _host.SessionId : flag))
        {
            startInfo.ArgumentList.Add(flag);
        }

        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        startInfo.Environment["DOTNET_ENVIRONMENT"] = "Production";
        startInfo.Environment["LOCALAPPDATA"] = _stateRoot;
        startInfo.Environment["XDG_STATE_HOME"] = _stateRoot;
        foreach (var key in startInfo.Environment.Keys.Where(key => key.StartsWith("DOTNETEFCOREMCP_", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            startInfo.Environment.Remove(key);
        }

        startInfo.Environment[EfCoreSurface.SessionStateDirectoryVariable] = _stateRoot;

        using var process = Process.Start(startInfo)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(120));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        return (process.ExitCode, await stdout, await stderr);
    }
}
