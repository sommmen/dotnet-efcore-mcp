using System.Text.Json;
using DotnetEfCoreMcp.Server.Updates;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotnetEfCoreMcp.Server.Tests.Updates;

/// <summary>
/// Guards the single highest-severity failure mode of the update notification: writing anything to
/// stdout while running as a stdio MCP server.
/// </summary>
/// <remarks>
/// <para>
/// The MCP stdio transport specifies that a server "MUST NOT write anything to stdout that is not a
/// valid MCP message". A stray notice there does not degrade the feature - it corrupts the JSON-RPC
/// stream and breaks the session outright, which is far worse than never notifying at all.
/// </para>
/// <para>
/// This is exactly the kind of bug that passes a casual review (the notice looks right in a
/// terminal) and only shows up as a mystifying client-side parse failure, so it is pinned down
/// directly rather than trusted to code review.
/// </para>
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ConsoleOutputCollection
{
    public const string Name = "Console output";
}

[Collection(ConsoleOutputCollection.Name)]
public class UpdateNotificationStdoutTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "efcore-update-stdout", Guid.NewGuid().ToString("n"));

    private sealed class StubLookup(IReadOnlyList<string> versions) : INuGetVersionLookup
    {
        public Task<IReadOnlyList<string>> GetVersionsAsync(string packageId, CancellationToken cancellationToken) =>
            Task.FromResult(versions);
    }

    private UpdateChecker Checker(IReadOnlyList<string> versions)
    {
        string? Read(string name) => null;

        return new UpdateChecker(
            new StubLookup(versions),
            new UpdateCheckCache(Path.Combine(_root, "update-check.json"), TimeProvider.System),
            new UpdateCheckGate(enabledByConfiguration: true, Read),
            new InstallMethodDetector(
                Path.Combine(
                    "Users", "dev", ".dotnet", "tools", ".store", "dotnetefcoremcp.server", "0.1.32-preview"),
                Path.Combine(_root, "cwd"),
                Read),
            "0.1.32-preview");
    }

    [Fact]
    public async Task Writes_nothing_to_stdout_even_when_an_update_is_found()
    {
        var original = Console.Out;
        var captured = new StringWriter();

        try
        {
            Console.SetOut(captured);

            var service = new UpdateNotificationService(
                Checker(["0.1.32-preview", "0.1.39-preview"]),
                NullLogger<UpdateNotificationService>.Instance);

            await service.StartAsync(CancellationToken.None);
            await WaitForResultAsync(service);
            await service.StopAsync(CancellationToken.None);

            // The check must have genuinely found an update, or this test would pass vacuously.
            Assert.True(service.Result?.UpdateAvailable);
            Assert.Equal(string.Empty, captured.ToString());
        }
        finally
        {
            Console.SetOut(original);
        }
    }

    [Fact]
    public async Task Reports_the_notice_through_the_result_rather_than_the_console()
    {
        var service = new UpdateNotificationService(
            Checker(["0.1.32-preview", "0.1.39-preview"]),
            NullLogger<UpdateNotificationService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await WaitForResultAsync(service);
        await service.StopAsync(CancellationToken.None);

        var notice = service.Result?.Notice;

        Assert.NotNull(notice);
        Assert.Contains("0.1.39-preview", notice);
        Assert.Contains("dotnet tool update --global", notice);

        // A notice must stay on one line: the stdio transport forbids embedded newlines in
        // messages, and this text can end up inside one.
        Assert.DoesNotContain('\n', notice);
    }

    [Fact]
    public async Task Produces_a_notice_that_survives_json_encoding()
    {
        var service = new UpdateNotificationService(
            Checker(["0.1.32-preview", "0.1.39-preview"]),
            NullLogger<UpdateNotificationService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await WaitForResultAsync(service);
        await service.StopAsync(CancellationToken.None);

        // The notice travels inside a tool result, so it has to round-trip through JSON intact.
        var result = service.Result;
        Assert.NotNull(result);
        Assert.True(result.UpdateAvailable);
        Assert.NotNull(result.Notice);
        var json = JsonSerializer.Serialize(new { notice = result.Notice });

        Assert.Equal(
            result.Notice,
            JsonDocument.Parse(json).RootElement.GetProperty("notice").GetString());
    }

    [Fact]
    public async Task Starts_and_stops_cleanly_when_the_feed_is_unreachable()
    {
        var service = new UpdateNotificationService(
            Checker([]),
            NullLogger<UpdateNotificationService>.Instance);

        // An offline machine must not stop the server from starting, which is the whole reason
        // the check runs in the background rather than during initialize.
        await service.StartAsync(CancellationToken.None);
        await WaitForResultAsync(service);
        await service.StopAsync(CancellationToken.None);

        Assert.False(service.Result?.UpdateAvailable ?? false);
    }

    /// <summary>
    /// Waits for the background check to publish a result, with a bound so a regression fails the
    /// test rather than hanging the suite.
    /// </summary>
    private static async Task WaitForResultAsync(UpdateNotificationService service)
    {
        for (var attempt = 0; attempt < 100 && service.Result is null; attempt++)
        {
            await Task.Delay(20);
        }
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
