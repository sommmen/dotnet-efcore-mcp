using System.Diagnostics;
using System.Text.Json;

namespace Launcher;

/// <summary>
/// The "MCP server" side of this PoC. Locates TargetApp's own build output (its
/// runtimeconfig.json + deps.json) and launches QueryHost.dll as a child process using
/// <c>dotnet exec --runtimeconfig ... --depsfile ...</c> - the same target-runtime invocation
/// pattern <c>dotnet ef</c> uses for its design-time tooling. QueryHost.dll is compiled
/// independently of TargetApp; at runtime its dependencies (EF Core, the SqlServer provider) are
/// resolved from TargetApp's dependency closure because of the runtimeconfig/depsfile pair
/// passed on the command line, not from QueryHost's own bin output.
///
/// See ../README.md for the full write-up and how to run this end to end.
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var pocRoot = FindPocRoot();
        var targetAppDir = Path.Combine(pocRoot, "TargetApp", "bin", "Debug", "net10.0");
        var queryHostDir = Path.Combine(pocRoot, "QueryHost", "bin", "Debug", "net10.0");

        var targetAssemblyPath = Path.Combine(targetAppDir, "TargetApp.dll");
        var targetRuntimeConfig = Path.Combine(targetAppDir, "TargetApp.runtimeconfig.json");
        var targetDepsFile = Path.Combine(targetAppDir, "TargetApp.deps.json");
        var queryHostDll = Path.Combine(queryHostDir, "QueryHost.dll");

        foreach (var (label, path) in new[]
                 {
                     ("TargetApp.dll", targetAssemblyPath),
                     ("TargetApp.runtimeconfig.json", targetRuntimeConfig),
                     ("TargetApp.deps.json", targetDepsFile),
                     ("QueryHost.dll", queryHostDll),
                 })
        {
            if (!File.Exists(path))
            {
                Console.Error.WriteLine($"[Launcher] ERROR: expected {label} at '{path}' but it does not exist.");
                Console.Error.WriteLine("[Launcher] Build both projects first - see poc/OutOfProcessQueryHost/README.md.");
                return 3;
            }
        }

        var connectionString = args.Length > 0
            ? args[0]
            : @"Server=(localdb)\MSSQLLocalDB;Database=EfCoreMcpPoc;Trusted_Connection=True;TrustServerCertificate=True;";
        var predicate = args.Length > 1 ? args[1] : "Price > 20";

        const string dbContextTypeName = "TargetApp.CatalogDbContext";
        const string dbSetPropertyName = "Products";

        Console.WriteLine($"[Launcher] pid={Environment.ProcessId}");
        Console.WriteLine($"[Launcher] target app runtimeconfig: {targetRuntimeConfig}");
        Console.WriteLine($"[Launcher] target app depsfile:     {targetDepsFile}");
        Console.WriteLine($"[Launcher] query host dll:          {queryHostDll}");
        Console.WriteLine($"[Launcher] predicate:                Where(\"{predicate}\")");
        Console.WriteLine();

        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add("--runtimeconfig");
        startInfo.ArgumentList.Add(targetRuntimeConfig);
        startInfo.ArgumentList.Add("--depsfile");
        startInfo.ArgumentList.Add(targetDepsFile);
        startInfo.ArgumentList.Add(queryHostDll);
        startInfo.ArgumentList.Add(targetAssemblyPath);
        startInfo.ArgumentList.Add(dbContextTypeName);
        startInfo.ArgumentList.Add(dbSetPropertyName);
        startInfo.ArgumentList.Add(connectionString);
        startInfo.ArgumentList.Add(predicate);

        Console.WriteLine($"[Launcher] launching: dotnet {string.Join(' ', startInfo.ArgumentList.Select(QuoteIfNeeded))}");
        Console.WriteLine();

        using var process = new Process { StartInfo = startInfo };
        var stdout = new System.Text.StringBuilder();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                return;
            }

            stdout.AppendLine(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                Console.WriteLine($"  [child stderr] {e.Data}");
            }
        };

        var stopwatch = Stopwatch.StartNew();
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync();
        stopwatch.Stop();

        Console.WriteLine();
        Console.WriteLine($"[Launcher] child process pid={process.Id} exited with code {process.ExitCode} in {stopwatch.ElapsedMilliseconds}ms");

        var lastLine = stdout.ToString()
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault();

        if (lastLine is null)
        {
            Console.Error.WriteLine("[Launcher] ERROR: child process produced no stdout output.");
            return 4;
        }

        using var jsonDoc = JsonDocument.Parse(lastLine);
        var root = jsonDoc.RootElement;
        if (!root.GetProperty("success").GetBoolean())
        {
            Console.Error.WriteLine($"[Launcher] child process reported failure: {root.GetProperty("error").GetString()}");
            return 5;
        }

        Console.WriteLine();
        Console.WriteLine("[Launcher] ===== result from out-of-process query host =====");
        Console.WriteLine($"[Launcher] child pid:              {root.GetProperty("pid").GetInt32()}");
        Console.WriteLine($"[Launcher] child framework:        {root.GetProperty("framework").GetString()}");
        Console.WriteLine($"[Launcher] EF Core assembly (child):{root.GetProperty("efCoreAssemblyLocation").GetString()}");
        Console.WriteLine($"[Launcher] row count:               {root.GetProperty("rowCount").GetInt32()}");
        Console.WriteLine();

        foreach (var row in root.GetProperty("rows").EnumerateArray())
        {
            Console.WriteLine("  " + row);
        }

        return 0;
    }

    private static string QuoteIfNeeded(string arg) => arg.Contains(' ') ? $"\"{arg}\"" : arg;

    private static string FindPocRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 6 && dir is not null; i++)
        {
            if (dir.Name == "Launcher" && dir.Parent is not null)
            {
                return dir.Parent.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            $"Could not locate the PoC root directory by walking up from '{AppContext.BaseDirectory}'.");
    }
}
