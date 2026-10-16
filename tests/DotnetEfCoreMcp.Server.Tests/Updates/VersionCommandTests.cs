using DotnetEfCoreMcp.Server.Updates;

namespace DotnetEfCoreMcp.Server.Tests.Updates;

/// <summary>
/// Covers the <c>version</c> CLI verb.
/// </summary>
/// <remarks>
/// This verb is the one update surface allowed to write to stdout, because CLI verbs are dispatched
/// and returned before the stdio MCP server opens its JSON-RPC channel.
/// </remarks>
public class VersionCommandTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "efcore-version-cmd", Guid.NewGuid().ToString("n"));

    private sealed class StubLookup(IReadOnlyList<string> versions) : INuGetVersionLookup
    {
        public Task<IReadOnlyList<string>> GetVersionsAsync(string packageId, CancellationToken cancellationToken) =>
            Task.FromResult(versions);
    }

    private UpdateChecker Checker(IReadOnlyList<string> versions, string installedVersion = "0.1.32-preview")
    {
        string? Read(string name) => null;

        return new UpdateChecker(
            new StubLookup(versions),
            new UpdateCheckCache(Path.Combine(_root, "update-check.json"), TimeProvider.System),
            new UpdateCheckGate(enabledByConfiguration: true, Read),
            new InstallMethodDetector(
                @"C:\Users\dev\.dotnet\tools\.store\dotnetefcoremcp.server\0.1.32-preview",
                Path.Combine(_root, "cwd"),
                Read),
            installedVersion);
    }

    private static (string Output, string Error) Capture(Func<TextWriter, TextWriter, Task<int>> run)
    {
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = run(output, error).GetAwaiter().GetResult();

        Assert.Equal(0, exitCode);
        return (output.ToString(), error.ToString());
    }

    [Fact]
    public void Reports_the_running_version_without_touching_the_network()
    {
        // No --check-updates, so a null checker is safe: nothing may dereference it.
        var (output, _) = Capture((out_, err) =>
            VersionCommand.RunAsync(["version"], out_, err, checker: null));

        Assert.Contains("dotnet-efcore-mcp", output);
        Assert.Contains("Installed via:", output);
    }

    [Fact]
    public void Reports_an_available_update()
    {
        var (output, _) = Capture((out_, err) => VersionCommand.RunAsync(
            ["version", "--check-updates"], out_, err, Checker(["0.1.32-preview", "0.1.39-preview"])));

        Assert.Contains("0.1.39-preview", output);
        Assert.Contains("update available", output);
    }

    [Fact]
    public void Reports_being_up_to_date()
    {
        var (output, _) = Capture((out_, err) => VersionCommand.RunAsync(
            ["version", "--check-updates"], out_, err, Checker(["0.1.32-preview"])));

        Assert.Contains("up to date", output);
    }

    [Fact]
    public void Reports_an_unreachable_feed_without_failing()
    {
        var (output, _) = Capture((out_, err) => VersionCommand.RunAsync(
            ["version", "--check-updates"], out_, err, Checker([])));

        Assert.Contains("not checked", output);
    }

    [Fact]
    public void Prints_usage_on_help()
    {
        var (output, _) = Capture((out_, err) =>
            VersionCommand.RunAsync(["version", "--help"], out_, err, checker: null));

        Assert.Contains("Usage: dotnet-efcore-mcp version", output);
        Assert.Contains(VersionCommand.CheckUpdatesFlag, output);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
