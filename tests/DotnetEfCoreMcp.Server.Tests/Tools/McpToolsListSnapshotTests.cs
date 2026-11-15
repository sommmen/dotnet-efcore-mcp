using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DotnetEfCoreMcp.Server.Tests.TestSupport;

namespace DotnetEfCoreMcp.Server.Tests.Tools;

/// <summary>Regression gate for the MCP surface the server advertises. Starts the real server
/// process, performs the stdio handshake and compares the normalized <c>tools/list</c> result
/// (plus <c>serverInfo.name</c> and <c>instructions</c>) with the committed golden file
/// <c>Tools/Snapshots/tools-list.json</c>.
/// <para>To accept an intentional change, rerun with <c>UPDATE_SNAPSHOTS=1</c> (for example
/// <c>$env:UPDATE_SNAPSHOTS = '1'; dotnet test --filter-class *McpToolsListSnapshotTests</c>) and
/// commit the rewritten file.</para>
/// <para>Runs in the serialized collection for the same reason as <see cref="McpServerShutdownTests"/>:
/// the child server inherits this process's environment and competes for CPU.</para></summary>
[Collection(ApplicationFactoryEnvironmentCollection.Name)]
public sealed class McpToolsListSnapshotTests
{
    private const string UpdateVariable = "UPDATE_SNAPSHOTS";

    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    [Fact]
    public async Task ToolsList_MatchesTheCommittedSnapshot()
    {
        var actual = await CaptureSurfaceAsync(TestContext.Current.CancellationToken);
        var snapshotPath = GetSnapshotPath();

        if (Environment.GetEnvironmentVariable(UpdateVariable) is "1" or "true")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(snapshotPath)!);
            await File.WriteAllTextAsync(snapshotPath, actual, new UTF8Encoding(false), TestContext.Current.CancellationToken);
            return;
        }

        Assert.True(
            File.Exists(snapshotPath),
            $"Snapshot '{snapshotPath}' does not exist. Set {UpdateVariable}=1 and rerun this test to create it.");

        var expected = Normalize(await File.ReadAllTextAsync(snapshotPath, TestContext.Current.CancellationToken));
        if (expected != actual)
        {
            Assert.Fail(
                $"The advertised MCP surface differs from '{snapshotPath}' (- expected, + actual).{Environment.NewLine}" +
                $"If the change is intentional, rerun with {UpdateVariable}=1 and commit the snapshot.{Environment.NewLine}{Environment.NewLine}" +
                LineDiff.Render(expected, actual));
        }
    }

    private static async Task<string> CaptureSurfaceAsync(CancellationToken cancellationToken)
    {
        using var process = StartServer();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(90));

            await SendAsync(process, """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"tools-list-snapshot-test","version":"1"}}}""", timeout.Token);
            var initialize = await ReadResponseAsync(process, 1, timeout.Token);

            await SendAsync(process, """{"jsonrpc":"2.0","method":"notifications/initialized"}""", timeout.Token);
            await SendAsync(process, """{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}""", timeout.Token);
            var toolsList = await ReadResponseAsync(process, 2, timeout.Token);

            return BuildSnapshot(initialize, toolsList);
        }
        finally
        {
            process.StandardInput.Close();
            if (!process.WaitForExit(TimeSpan.FromSeconds(30)))
            {
                process.Kill(entireProcessTree: true);
            }
        }
    }

    private static string BuildSnapshot(JsonNode initialize, JsonNode toolsList)
    {
        var result = initialize["result"] ?? throw new InvalidOperationException($"initialize returned no result: {initialize}");
        var tools = (toolsList["result"]?["tools"] as JsonArray)
            ?? throw new InvalidOperationException($"tools/list returned no tools: {toolsList}");

        var sortedTools = new JsonArray(tools
            .OrderBy(tool => tool!["name"]!.GetValue<string>(), StringComparer.Ordinal)
            .Select(tool => tool!.DeepClone())
            .ToArray());

        var snapshot = new JsonObject
        {
            ["serverInfo"] = new JsonObject { ["name"] = result["serverInfo"]?["name"]?.DeepClone() },
            ["instructions"] = result["instructions"]?.DeepClone(),
            ["tools"] = sortedTools,
        };

        return Normalize(snapshot.ToJsonString(IndentedJson));
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n").TrimEnd('\n') + "\n";

    private static async Task SendAsync(Process process, string message, CancellationToken cancellationToken)
    {
        await process.StandardInput.WriteLineAsync(message.AsMemory(), cancellationToken);
        await process.StandardInput.FlushAsync(cancellationToken);
    }

    private static async Task<JsonNode> ReadResponseAsync(Process process, int id, CancellationToken cancellationToken)
    {
        while (await process.StandardOutput.ReadLineAsync(cancellationToken) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var message = JsonNode.Parse(line);
            if (message?["id"]?.GetValue<int>() == id)
            {
                return message;
            }
        }

        throw new InvalidOperationException($"The server closed stdout before answering request {id}.");
    }

    private static Process StartServer()
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            ArgumentList = { FixturePaths.ServerDllPath },
            WorkingDirectory = Path.GetDirectoryName(FixturePaths.ServerDllPath)!,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        // Keep the advertised surface independent of the machine running the test: Development mode
        // and DOTNETEFCOREMCP_* overrides can change what the server registers or prints.
        startInfo.Environment["DOTNET_ENVIRONMENT"] = "Production";
        startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        foreach (var key in startInfo.Environment.Keys.Where(key => key.StartsWith("DOTNETEFCOREMCP_", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            startInfo.Environment.Remove(key);
        }

        var process = new Process { StartInfo = startInfo };
        process.Start();
        // stderr is not asserted on; drain it so a chatty server can never block on a full pipe.
        process.ErrorDataReceived += (_, _) => { };
        process.BeginErrorReadLine();
        return process;
    }

    private static string GetSnapshotPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DotnetEfCoreMcp.Server.Tests.csproj")))
        {
            directory = directory.Parent;
        }

        if (directory is null)
        {
            throw new DirectoryNotFoundException("Could not locate the test project directory from " + AppContext.BaseDirectory);
        }

        return Path.Combine(directory.FullName, "Tools", "Snapshots", "tools-list.json");
    }

    /// <summary>Minimal LCS line diff, enough to point at the changed tool or property.</summary>
    private static class LineDiff
    {
        private const int ContextLines = 2;

        public static string Render(string expected, string actual)
        {
            var left = expected.Split('\n');
            var right = actual.Split('\n');

            var lcs = new int[left.Length + 1, right.Length + 1];
            for (var i = left.Length - 1; i >= 0; i--)
            {
                for (var j = right.Length - 1; j >= 0; j--)
                {
                    lcs[i, j] = left[i] == right[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
                }
            }

            var lines = new List<(char Kind, string Text)>();
            int a = 0, b = 0;
            while (a < left.Length && b < right.Length)
            {
                if (left[a] == right[b])
                {
                    lines.Add((' ', left[a]));
                    a++;
                    b++;
                }
                else if (lcs[a + 1, b] >= lcs[a, b + 1])
                {
                    lines.Add(('-', left[a++]));
                }
                else
                {
                    lines.Add(('+', right[b++]));
                }
            }

            while (a < left.Length)
            {
                lines.Add(('-', left[a++]));
            }

            while (b < right.Length)
            {
                lines.Add(('+', right[b++]));
            }

            var keep = new bool[lines.Count];
            for (var i = 0; i < lines.Count; i++)
            {
                if (lines[i].Kind == ' ')
                {
                    continue;
                }

                for (var k = Math.Max(0, i - ContextLines); k <= Math.Min(lines.Count - 1, i + ContextLines); k++)
                {
                    keep[k] = true;
                }
            }

            var output = new StringBuilder();
            var skipped = false;
            for (var i = 0; i < lines.Count; i++)
            {
                if (!keep[i])
                {
                    skipped = true;
                    continue;
                }

                if (skipped)
                {
                    output.AppendLine("  ...");
                    skipped = false;
                }

                output.Append(lines[i].Kind).Append(' ').AppendLine(lines[i].Text);
            }

            return output.ToString();
        }
    }
}
