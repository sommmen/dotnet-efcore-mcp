using System.Text.Json;
using System.Text.Json.Nodes;
using DotnetEfCoreMcp.Server.Hooks;

namespace DotnetEfCoreMcp.Server.Tests.Hooks;

/// <summary>
/// Covers writing, merging, and removing repo-local hook configuration.
/// </summary>
public class HookInstallerTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "efcore-hook-install-tests", Guid.NewGuid().ToString("n"));

    public HookInstallerTests() => Directory.CreateDirectory(_root);

    [Theory]
    [InlineData(HookClient.Claude)]
    [InlineData(HookClient.Copilot)]
    [InlineData(HookClient.Codex)]
    public void Install_WritesConfigForEachClient(HookClient client)
    {
        var result = new HookInstaller("1.2.3").Install(client, _root);

        Assert.True(result.Changed);
        Assert.True(File.Exists(result.FilePath));

        var json = File.ReadAllText(result.FilePath);
        Assert.Contains(client is HookClient.Copilot ? "postToolUse" : "PostToolUse", json, StringComparison.Ordinal);
        Assert.Contains("hooks", json, StringComparison.Ordinal);
        Assert.Contains("DotnetEfCoreMcp.Server@1.2.3", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Install_AddsPrereleaseFlagOnlyWhenVersionIsNotPinned()
    {
        // dnx resolves the latest *stable* release by default, and releases are preview-only, so an
        // unpinned command would otherwise fail to resolve the package at all.
        new HookInstaller(packageVersion: null).Install(HookClient.Claude, _root);
        var unpinned = File.ReadAllText(HookInstaller.GetConfigPath(HookClient.Claude, _root));
        Assert.Contains("--prerelease", unpinned, StringComparison.Ordinal);

        new HookInstaller("1.2.3-preview").Install(HookClient.Claude, _root, force: true);
        var pinned = File.ReadAllText(HookInstaller.GetConfigPath(HookClient.Claude, _root));
        Assert.Contains("DotnetEfCoreMcp.Server@1.2.3-preview", pinned, StringComparison.Ordinal);
        Assert.DoesNotContain("--prerelease", pinned, StringComparison.Ordinal);
    }

    [Fact]
    public void Install_UsesExecFormForCopilotSoWindowsPathsNeedNoShellQuoting()
    {
        var installer = new HookInstaller("1.2.3", isWindows: false);
        installer.Install(HookClient.Copilot, _root);

        var root = ReadJson(HookInstaller.GetConfigPath(HookClient.Copilot, _root));

        // Copilot requires an explicit schema version.
        Assert.Equal(1, root["version"]!.GetValue<int>());

        var handler = root["hooks"]!["postToolUse"]!.AsArray()[0]!.AsObject();
        Assert.Equal("dnx", handler["exec"]!.GetValue<string>());
        Assert.NotNull(handler["args"]);
        Assert.Null(handler["command"]);
    }

    [Fact]
    public void Install_LaunchesCopilotHooksThroughDotnetOnWindows()
    {
        // dnx is a dnx.cmd batch wrapper on Windows; Node spawns exec without a shell, so "dnx"
        // gives ENOENT and "dnx.cmd" gives EINVAL. dotnet.exe is a real executable.
        new HookInstaller("1.2.3", isWindows: true).Install(HookClient.Copilot, _root);

        var handler = ReadJson(HookInstaller.GetConfigPath(HookClient.Copilot, _root))
            ["hooks"]!["postToolUse"]!.AsArray()[0]!.AsObject();

        Assert.Equal("dotnet", handler["exec"]!.GetValue<string>());
        Assert.Equal(
            ["dnx", "DotnetEfCoreMcp.Server@1.2.3", "--yes", "--", "hooks", "remind", "--client", "copilot", "--scope", "workspace"],
            handler["args"]!.AsArray().Select(a => a!.GetValue<string>()));
    }

    [Theory]
    [InlineData(HookClient.Claude)]
    [InlineData(HookClient.Codex)]
    public void Install_LaunchesShellHooksThroughDotnetOnWindows(HookClient client)
    {
        // Git Bash does not resolve dnx.cmd either, so the shell command line uses dotnet too.
        new HookInstaller("1.2.3", isWindows: true).Install(client, _root);

        var json = File.ReadAllText(HookInstaller.GetConfigPath(client, _root));

        Assert.Contains("\"dotnet dnx DotnetEfCoreMcp.Server@1.2.3 --yes -- hooks remind", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Install_KeepsDnxOutsideWindows()
    {
        var installer = new HookInstaller("1.2.3", isWindows: false);
        installer.Install(HookClient.Copilot, _root);
        installer.Install(HookClient.Claude, _root);

        var copilot = ReadJson(HookInstaller.GetConfigPath(HookClient.Copilot, _root))
            ["hooks"]!["postToolUse"]!.AsArray()[0]!.AsObject();
        Assert.Equal("dnx", copilot["exec"]!.GetValue<string>());
        Assert.Equal("DotnetEfCoreMcp.Server@1.2.3", copilot["args"]!.AsArray()[0]!.GetValue<string>());

        Assert.Contains(
            "\"dnx DotnetEfCoreMcp.Server@1.2.3 --yes -- hooks remind",
            File.ReadAllText(HookInstaller.GetConfigPath(HookClient.Claude, _root)),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Install_ReplacesPreviousDnxHooksWithTheWindowsLauncher()
    {
        new HookInstaller("1.2.3", isWindows: false).Install(HookClient.Copilot, _root);
        var result = new HookInstaller("1.2.3", isWindows: true).Install(HookClient.Copilot, _root);

        var postToolUse = ReadJson(HookInstaller.GetConfigPath(HookClient.Copilot, _root))["hooks"]!["postToolUse"]!.AsArray();

        Assert.True(result.Changed);
        Assert.Single(postToolUse);
        Assert.Equal("dotnet", postToolUse[0]!["exec"]!.GetValue<string>());
    }

    [Fact]
    public void Install_UsesMatcherGroupsForClaude()
    {
        new HookInstaller("1.2.3").Install(HookClient.Claude, _root);

        var root = ReadJson(HookInstaller.GetConfigPath(HookClient.Claude, _root));
        var group = root["hooks"]!["PostToolUse"]!.AsArray()[0]!.AsObject();

        // Claude nests handlers inside a matcher group rather than listing them directly.
        Assert.NotNull(group["matcher"]);
        var handler = group["hooks"]!.AsArray()[0]!.AsObject();
        Assert.Equal("command", handler["type"]!.GetValue<string>());
        Assert.Contains("hooks remind", handler["command"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public void Install_IsIdempotent()
    {
        var installer = new HookInstaller("1.2.3");
        var path = HookInstaller.GetConfigPath(HookClient.Claude, _root);

        installer.Install(HookClient.Claude, _root);
        var afterFirst = File.ReadAllText(path);

        var second = installer.Install(HookClient.Claude, _root);

        Assert.False(second.Changed);
        Assert.Equal(afterFirst, File.ReadAllText(path));
    }

    [Fact]
    public void Install_ReplacesStaleVersionWithoutDuplicating()
    {
        var path = HookInstaller.GetConfigPath(HookClient.Claude, _root);

        new HookInstaller("1.0.0").Install(HookClient.Claude, _root);
        new HookInstaller("2.0.0").Install(HookClient.Claude, _root);

        var root = ReadJson(path);
        var groups = root["hooks"]!["PostToolUse"]!.AsArray();

        Assert.Single(groups);
        Assert.Contains("2.0.0", File.ReadAllText(path), StringComparison.Ordinal);
        Assert.DoesNotContain("1.0.0", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public void Install_EmitsOnlyFieldsTheClientsDocument()
    {
        // Copilot drops hook items that fail validation and Codex hashes the definition for its
        // trust prompt, so an unknown property could silently disable the hook.
        var installer = new HookInstaller("1.2.3");

        installer.Install(HookClient.Claude, _root);
        var claudeHandler = ReadJson(HookInstaller.GetConfigPath(HookClient.Claude, _root))
            ["hooks"]!["PostToolUse"]!.AsArray()[0]!["hooks"]!.AsArray()[0]!.AsObject();
        Assert.All(claudeHandler, pair => Assert.Contains(pair.Key, (string[])["type", "command", "timeout"]));

        installer.Install(HookClient.Copilot, _root);
        var copilotHandler = ReadJson(HookInstaller.GetConfigPath(HookClient.Copilot, _root))
            ["hooks"]!["postToolUse"]!.AsArray()[0]!.AsObject();
        Assert.All(copilotHandler, pair =>
            Assert.Contains(pair.Key, (string[])["type", "exec", "args", "timeoutSec", "matcher"]));
    }

    [Fact]
    public void Install_ReplacesHooksTaggedByOlderBuilds()
    {
        var path = HookInstaller.GetConfigPath(HookClient.Claude, _root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // Earlier builds tagged handlers with a custom marker property instead of relying on the
        // command text; upgrading over one must replace it rather than duplicate it.
        File.WriteAllText(path, """
            {
              "hooks": {
                "PostToolUse": [
                  {
                    "matcher": "Write",
                    "hooks": [ { "type": "command", "x-dotnet-efcore-mcp": "dotnet-efcore-mcp:linq-validation", "command": "legacy" } ]
                  }
                ]
              }
            }
            """);

        new HookInstaller("1.2.3").Install(HookClient.Claude, _root);

        Assert.Single(ReadJson(path)["hooks"]!["PostToolUse"]!.AsArray());
        Assert.DoesNotContain("legacy", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public void Install_PreservesUnrelatedSettingsAndHooks()
    {
        var path = HookInstaller.GetConfigPath(HookClient.Claude, _root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """
            {
              "permissions": { "allow": ["Bash(git status)"] },
              "hooks": {
                "PostToolUse": [
                  { "matcher": "Write", "hooks": [ { "type": "command", "command": "my-formatter" } ] }
                ]
              }
            }
            """);

        new HookInstaller("1.2.3").Install(HookClient.Claude, _root);

        var root = ReadJson(path);

        // Unrelated settings and the user's own hook must survive the merge.
        Assert.NotNull(root["permissions"]);
        var groups = root["hooks"]!["PostToolUse"]!.AsArray();
        Assert.Equal(2, groups.Count);
        Assert.Contains("my-formatter", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public void Install_ToleratesCommentsAndTrailingCommas()
    {
        var path = HookInstaller.GetConfigPath(HookClient.Claude, _root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // Claude settings files are routinely hand-edited into JSONC.
        File.WriteAllText(path, """
            {
              // developer note
              "permissions": { "allow": ["Bash(git status)"] },
            }
            """);

        var result = new HookInstaller("1.2.3").Install(HookClient.Claude, _root);

        Assert.True(result.Changed);
        Assert.NotNull(ReadJson(path)["permissions"]);
    }

    [Fact]
    public void Install_SkipsFilesThatAreNotJsonObjects()
    {
        var path = HookInstaller.GetConfigPath(HookClient.Claude, _root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "this is not json");

        var result = new HookInstaller("1.2.3").Install(HookClient.Claude, _root);

        // Never clobber a file we cannot understand.
        Assert.False(result.Changed);
        Assert.Equal("this is not json", File.ReadAllText(path));
    }

    [Fact]
    public void Uninstall_RemovesFileItCreated()
    {
        var installer = new HookInstaller("1.2.3");
        installer.Install(HookClient.Codex, _root);

        var result = installer.Uninstall(HookClient.Codex, _root);

        Assert.True(result.Changed);
        Assert.False(File.Exists(HookInstaller.GetConfigPath(HookClient.Codex, _root)));
    }

    [Fact]
    public void Uninstall_KeepsFileWhenOtherContentRemains()
    {
        var path = HookInstaller.GetConfigPath(HookClient.Claude, _root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """{ "permissions": { "allow": [] } }""");

        var installer = new HookInstaller("1.2.3");
        installer.Install(HookClient.Claude, _root);
        installer.Uninstall(HookClient.Claude, _root);

        Assert.True(File.Exists(path));

        var root = ReadJson(path);
        Assert.NotNull(root["permissions"]);
        Assert.DoesNotContain("DotnetEfCoreMcp", File.ReadAllText(path), StringComparison.Ordinal);

        // Uninstall should leave no trace, not an empty "hooks": {} husk.
        Assert.Null(root["hooks"]);
    }

    [Fact]
    public void Uninstall_ReportsWhenNothingIsInstalled()
    {
        var result = new HookInstaller("1.2.3").Uninstall(HookClient.Claude, _root);

        Assert.False(result.Changed);
        Assert.Contains("Nothing to remove", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Status_ReportsInstallationState()
    {
        var installer = new HookInstaller("1.2.3");

        Assert.Equal("not installed", installer.Status(HookClient.Claude, _root).Message);

        installer.Install(HookClient.Claude, _root);

        Assert.Equal("installed", installer.Status(HookClient.Claude, _root).Message);
    }

    [Fact]
    public void GetConfigPath_UsesTheLocationEachClientReads()
    {
        Assert.Equal(
            Path.Combine(_root, ".claude", "settings.json"),
            HookInstaller.GetConfigPath(HookClient.Claude, _root));

        Assert.Equal(
            Path.Combine(_root, ".github", "hooks", "dotnet-efcore-mcp.json"),
            HookInstaller.GetConfigPath(HookClient.Copilot, _root));

        Assert.Equal(
            Path.Combine(_root, ".codex", "hooks.json"),
            HookInstaller.GetConfigPath(HookClient.Codex, _root));
    }

    [Fact]
    public void Install_WritesValidJsonForEveryClient()
    {
        var installer = new HookInstaller("1.2.3");

        foreach (var client in (HookClient[])[HookClient.Claude, HookClient.Copilot, HookClient.Codex])
        {
            var result = installer.Install(client, _root);

            var exception = Record.Exception(() => JsonDocument.Parse(File.ReadAllText(result.FilePath)));
            Assert.Null(exception);
        }
    }
    [Fact]
    public void Install_WritesNativeCamelCaseEventsForCopilotOnly()
    {
        var installer = new HookInstaller("1.2.3");

        installer.Install(HookClient.Copilot, _root);
        var copilot = ReadJson(HookInstaller.GetConfigPath(HookClient.Copilot, _root));
        Assert.Equal(1, copilot["version"]!.GetValue<int>());
        Assert.Equal(["postToolUse", "sessionEnd", "sessionStart"],
            copilot["hooks"]!.AsObject().Select(pair => pair.Key).Order(StringComparer.Ordinal));

        foreach (var client in (HookClient[])[HookClient.Claude, HookClient.Codex])
        {
            installer.Install(client, _root);
            var other = ReadJson(HookInstaller.GetConfigPath(client, _root));
            Assert.Equal(["PostToolUse", "SessionEnd", "SessionStart"],
                other["hooks"]!.AsObject().Select(pair => pair.Key).Order(StringComparer.Ordinal));
        }
    }

    [Fact]
    public void Install_ReplacesPascalCaseCopilotEntriesWrittenByEarlierBuilds()
    {
        var path = HookInstaller.GetConfigPath(HookClient.Copilot, _root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, PascalCaseCopilotFile);

        var result = new HookInstaller("1.2.3").Install(HookClient.Copilot, _root);

        var hooks = ReadJson(path)["hooks"]!.AsObject();
        Assert.True(result.Changed);
        Assert.Equal(["postToolUse", "sessionEnd", "sessionStart"],
            hooks.Select(pair => pair.Key).Order(StringComparer.Ordinal));
        Assert.All(hooks, pair => Assert.Single(pair.Value!.AsArray()));
        Assert.DoesNotContain("1.0.0", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public void Install_KeepsUserHooksUnderPascalCaseKeysWhenMigratingCopilotEntries()
    {
        var path = HookInstaller.GetConfigPath(HookClient.Copilot, _root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """
            {
              "version": 1,
              "hooks": {
                "PostToolUse": [
                  { "type": "command", "command": "my-formatter" },
                  { "type": "command", "exec": "dnx", "args": ["DotnetEfCoreMcp.Server@1.0.0", "--", "hooks", "remind"] }
                ]
              }
            }
            """);

        new HookInstaller("1.2.3").Install(HookClient.Copilot, _root);

        var hooks = ReadJson(path)["hooks"]!.AsObject();
        var user = Assert.Single(hooks["PostToolUse"]!.AsArray());
        Assert.Equal("my-formatter", user!["command"]!.GetValue<string>());
        Assert.Single(hooks["postToolUse"]!.AsArray());
    }

    [Fact]
    public void Install_IsIdempotentForCopilot()
    {
        var installer = new HookInstaller("1.2.3");
        installer.Install(HookClient.Copilot, _root);

        var second = installer.Install(HookClient.Copilot, _root);

        Assert.False(second.Changed);
    }

    [Fact]
    public void Status_RecognizesPascalCaseCopilotEntries()
    {
        var path = HookInstaller.GetConfigPath(HookClient.Copilot, _root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, PascalCaseCopilotFile);

        Assert.Equal("installed", new HookInstaller("1.2.3").Status(HookClient.Copilot, _root).Message);
    }

    [Fact]
    public void Uninstall_RemovesPascalCaseCopilotEntriesWrittenByEarlierBuilds()
    {
        var path = HookInstaller.GetConfigPath(HookClient.Copilot, _root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, PascalCaseCopilotFile);

        var result = new HookInstaller("1.2.3").Uninstall(HookClient.Copilot, _root);

        Assert.True(result.Changed);
        Assert.False(File.Exists(path));
    }

    private const string PascalCaseCopilotFile = """
        {
          "version": 1,
          "hooks": {
            "SessionStart": [ { "type": "command", "exec": "dnx", "args": ["DotnetEfCoreMcp.Server@1.0.0", "--yes", "--", "hooks", "activate", "--client", "copilot", "--scope", "workspace"], "timeoutSec": 20 } ],
            "PostToolUse": [ { "type": "command", "exec": "dnx", "args": ["DotnetEfCoreMcp.Server@1.0.0", "--yes", "--", "hooks", "remind", "--client", "copilot", "--scope", "workspace"], "timeoutSec": 20, "matcher": "Write|Edit" } ],
            "SessionEnd": [ { "type": "command", "exec": "dnx", "args": ["DotnetEfCoreMcp.Server@1.0.0", "--yes", "--", "hooks", "cleanup", "--client", "copilot", "--scope", "workspace"], "timeoutSec": 20 } ]
          }
        }
        """;

    private static JsonObject ReadJson(string path) =>
        JsonNode.Parse(File.ReadAllText(path))!.AsObject();

    public void Dispose()
    {
        GC.SuppressFinalize(this);

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
