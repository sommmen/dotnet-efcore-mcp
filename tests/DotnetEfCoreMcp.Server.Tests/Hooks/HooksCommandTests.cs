using System.Text.Json;
using DotnetEfCoreMcp.Server.Hooks;

namespace DotnetEfCoreMcp.Server.Tests.Hooks;

/// <summary>
/// Covers the <c>hooks</c> CLI verb's argument handling and its fail-open output contract.
/// </summary>
public class HooksCommandTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "efcore-hook-cli-tests", Guid.NewGuid().ToString("n"));

    private readonly string? _previousStateDirectory =
        Environment.GetEnvironmentVariable("DOTNETEFCOREMCP_HOOKDATADIR");

    public HooksCommandTests()
    {
        Directory.CreateDirectory(_root);

        // Keep reminder state inside the test's own directory rather than the real user profile.
        Environment.SetEnvironmentVariable(
            "DOTNETEFCOREMCP_HOOKDATADIR", Path.Combine(_root, "state"));
    }

    [Fact]
    public void Run_InstallWritesConfigForRequestedClientOnly()
    {
        var exitCode = Run(["hooks", "install", "--client", "codex", "--path", _root], out var output);

        Assert.Equal(0, exitCode);
        Assert.Contains("codex", output, StringComparison.Ordinal);
        Assert.True(File.Exists(HookInstaller.GetConfigPath(HookClient.Codex, _root)));
        Assert.False(File.Exists(HookInstaller.GetConfigPath(HookClient.Claude, _root)));
    }

    [Fact]
    public void Run_AcceptsCommaSeparatedClients()
    {
        Run(["hooks", "install", "--client", "claude,codex", "--path", _root], out _);

        Assert.True(File.Exists(HookInstaller.GetConfigPath(HookClient.Claude, _root)));
        Assert.True(File.Exists(HookInstaller.GetConfigPath(HookClient.Codex, _root)));
        Assert.False(File.Exists(HookInstaller.GetConfigPath(HookClient.Copilot, _root)));
    }

    [Fact]
    public void Run_AcceptsOptionsInEqualsForm()
    {
        var exitCode = Run(["hooks", "install", "--client=codex", $"--path={_root}"], out _);

        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(HookInstaller.GetConfigPath(HookClient.Codex, _root)));
    }

    [Fact]
    public void Run_RemindEmitsReminderForLinqWrite()
    {
        const string payload = """
            {
              "session_id": "cli-1",
              "tool_name": "Edit",
              "tool_input": {
                "file_path": "/repo/src/Orders.cs",
                "new_string": "await _context.Orders.Where(o => o.Id > 1).ToListAsync();"
              }
            }
            """;

        var exitCode = Run(["hooks", "remind", "--client", "claude"], out var output, payload);

        Assert.Equal(0, exitCode);
        Assert.Contains("LINQ validation reminder", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{"tool_name":"Edit"}""")]
    public void Run_RemindAlwaysSucceedsWithParseableJson(string payload)
    {
        // Copilot CLI treats a failing hook as a warning and Claude surfaces stderr, so the hook
        // must never exit non-zero or print anything but a JSON object.
        var exitCode = Run(["hooks", "remind", "--client", "copilot"], out var output, payload);

        Assert.Equal(0, exitCode);

        var exception = Record.Exception(() => JsonDocument.Parse(output));
        Assert.Null(exception);
    }

    [Fact]
    public void Run_RemindWithUnknownClientStillEmitsReminder()
    {
        const string payload = """
            {
              "session_id": "cli-2",
              "tool_name": "Edit",
              "tool_input": { "file_path": "/repo/src/Orders.cs", "new_string": "_context.Orders.Where(o => o.Id > 1).ToListAsync();" }
            }
            """;

        var exitCode = Run(["hooks", "remind", "--client", "nonsense"], out var output, payload);

        Assert.Equal(0, exitCode);
        Assert.Contains("LINQ validation reminder", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Run_CleanupSucceedsWithoutAnyState()
    {
        var exitCode = Run(["hooks", "cleanup", "--client", "claude"], out var output, """{"session_id":"absent"}""");

        Assert.Equal(0, exitCode);
        Assert.Equal("{}", output);
    }

    [Fact]
    public void Run_StatusReportsEachClient()
    {
        var exitCode = Run(["hooks", "status", "--path", _root], out var output);

        Assert.Equal(0, exitCode);
        foreach (var client in (string[])["claude", "copilot", "codex"])
        {
            Assert.Contains($"[{client}] not installed", output, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Run_UnknownSubCommandFails()
    {
        var exitCode = Run(["hooks", "bogus"], out _, error: out var error);

        Assert.Equal(1, exitCode);
        Assert.Contains("Unknown", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Run_WithoutSubCommandFails()
    {
        var exitCode = Run(["hooks"], out _, error: out var error);

        Assert.Equal(1, exitCode);
        Assert.Contains("Usage", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Run_HelpSucceeds()
    {
        var exitCode = Run(["hooks", "--help"], out var output);

        Assert.Equal(0, exitCode);
        Assert.Contains("Usage", output, StringComparison.Ordinal);
    }

    [Fact]
    public void GetPackageVersion_ReturnsResolvableNuGetVersion()
    {
        // A version that does not match the published package would make every installed hook fail
        // to resolve, so it must never contain build metadata.
        var version = HooksCommand.GetPackageVersion();

        Assert.False(string.IsNullOrWhiteSpace(version));
        Assert.DoesNotContain('+', version);
    }

    private static int Run(string[] args, out string output, string input = "") =>
        Run(args, out output, out _, input);

    private static int Run(string[] args, out string output, out string error, string input = "")
    {
        var outputWriter = new StringWriter();
        var errorWriter = new StringWriter();

        var exitCode = HooksCommand.Run(args, outputWriter, errorWriter, new StringReader(input));

        output = outputWriter.ToString();
        error = errorWriter.ToString();
        return exitCode;
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);

        Environment.SetEnvironmentVariable("DOTNETEFCOREMCP_HOOKDATADIR", _previousStateDirectory);

        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Leftover temp state is harmless.
        }
    }
}
