using System.Diagnostics;
using System.Text.Json;
using DotnetEfCoreMcp.Server.Tests.TestSupport;

namespace DotnetEfCoreMcp.Server.Tests.Tools;

/// <summary>Drives the real server process over stdio exactly as an MCP client does - one request,
/// then stdin closes - and asserts the session's output survives shutdown. An exception thrown
/// while the host shuts down tears the process down before stdout is flushed, silently losing every
/// response written during the session, so the exit code alone is not enough to catch it.
/// <para>Runs in the serialized collection because the child server inherits this process's
/// environment, the same reason the other tests in that collection are serialized. Keeping it there
/// also stops a whole extra server process from competing for CPU with the Roslyn-compilation tests,
/// which enforce their own wall-clock compile timeout and start failing on a loaded machine.</para></summary>
[Collection(ApplicationFactoryEnvironmentCollection.Name)]
public sealed class McpServerShutdownTests
{
    private const string InitializeRequest =
        """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"shutdown-test","version":"1"}}}""";

    [Fact]
    public async Task ClosingStdin_ExitsCleanly_AndFlushesTheSessionsResponses()
    {
        using var process = StartServer();

        await process.StandardInput.WriteLineAsync(InitializeRequest);
        await process.StandardInput.FlushAsync(TestContext.Current.CancellationToken);
        process.StandardInput.Close();

        var stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        var output = await stdout;
        var errors = await stderr;

        Assert.Equal(0, process.ExitCode);
        Assert.DoesNotContain("Unhandled exception", errors, StringComparison.Ordinal);

        var responseLine = output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        Assert.NotNull(responseLine);

        using var response = JsonDocument.Parse(responseLine);
        Assert.Equal(1, response.RootElement.GetProperty("id").GetInt32());
        Assert.True(response.RootElement.TryGetProperty("result", out var result));
        Assert.Equal("2024-11-05", result.GetProperty("protocolVersion").GetString());
    }

    private static Process StartServer()
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo("dotnet")
            {
                ArgumentList = { FixturePaths.ServerDllPath },
                WorkingDirectory = Path.GetDirectoryName(FixturePaths.ServerDllPath)!,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };

        process.Start();
        return process;
    }
}
