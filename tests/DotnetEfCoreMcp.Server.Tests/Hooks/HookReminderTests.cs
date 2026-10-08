using System.Text.Json;
using DotnetEfCoreMcp.Server.Hooks;

namespace DotnetEfCoreMcp.Server.Tests.Hooks;

/// <summary>
/// Covers when a reminder fires, how often, and the envelope it is delivered in.
/// </summary>
public class HookReminderTests : IDisposable
{
    private readonly string _stateDirectory =
        Path.Combine(Path.GetTempPath(), "efcore-hook-tests", Guid.NewGuid().ToString("n"));

    [Fact]
    public void Evaluate_RemindsAfterLinqWrite()
    {
        var reminder = CreateReminder(out _);

        var response = reminder.Evaluate(LinqWritePayload("s1"), HookClient.Claude);

        Assert.Contains("LINQ validation reminder", response, StringComparison.Ordinal);
        Assert.Contains("preview_query_sql", response, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_EmitsBothEnvelopesSoEveryClientCanReadIt()
    {
        var reminder = CreateReminder(out _);

        var response = reminder.Evaluate(LinqWritePayload("s2"), HookClient.Claude);

        using var document = JsonDocument.Parse(response);
        var root = document.RootElement;

        // Copilot reads the top-level field; Claude and Codex read the nested one.
        Assert.True(root.TryGetProperty("additionalContext", out var flat));
        Assert.False(string.IsNullOrWhiteSpace(flat.GetString()));

        Assert.True(root.TryGetProperty("hookSpecificOutput", out var nested));
        Assert.Equal("PostToolUse", nested.GetProperty("hookEventName").GetString());
        Assert.False(string.IsNullOrWhiteSpace(nested.GetProperty("additionalContext").GetString()));
    }

    [Fact]
    public void Evaluate_ReturnsEmptyObjectForUnrelatedCalls()
    {
        var reminder = CreateReminder(out _);

        var payload = CreatePayload("s3", "Edit", new
        {
            file_path = "/repo/src/Util.cs",
            new_string = "var names = people.Where(p => p.Age > 18).ToList();",
        });

        Assert.Equal("{}", reminder.Evaluate(payload, HookClient.Claude));
    }

    [Fact]
    public void Evaluate_SuppressesSecondReminderWithinCooldown()
    {
        var reminder = CreateReminder(out var time);

        Assert.Contains("LINQ validation", reminder.Evaluate(LinqWritePayload("s4"), HookClient.Claude), StringComparison.Ordinal);

        time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal("{}", reminder.Evaluate(LinqWritePayload("s4"), HookClient.Claude));
    }

    [Fact]
    public void Evaluate_RemindsAgainAfterCooldownElapses()
    {
        var reminder = CreateReminder(out var time);

        reminder.Evaluate(LinqWritePayload("s5"), HookClient.Claude);

        time.Advance(HookReminder.ReminderCooldown + TimeSpan.FromMinutes(1));

        Assert.Contains("LINQ validation", reminder.Evaluate(LinqWritePayload("s5"), HookClient.Claude), StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_UsingTheMcpServerClearsTheCooldown()
    {
        var reminder = CreateReminder(out var time);

        reminder.Evaluate(LinqWritePayload("s6"), HookClient.Claude);
        time.Advance(TimeSpan.FromMinutes(1));

        // Validating against the model is the goal, so the next query starts from a clean slate
        // instead of waiting out a cooldown earned by the previous one.
        var mcpCall = CreatePayload("s6", "mcp__dotnet-efcore__run_query", new { query = "Orders" });
        Assert.Equal("{}", reminder.Evaluate(mcpCall, HookClient.Claude));

        Assert.Contains("LINQ validation", reminder.Evaluate(LinqWritePayload("s6"), HookClient.Claude), StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_SessionsAreIndependent()
    {
        var reminder = CreateReminder(out _);

        Assert.Contains("LINQ validation", reminder.Evaluate(LinqWritePayload("s7a"), HookClient.Claude), StringComparison.Ordinal);

        // A cooldown in one session must not silence a different one.
        Assert.Contains("LINQ validation", reminder.Evaluate(LinqWritePayload("s7b"), HookClient.Claude), StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_EntityReadsOnlyRemindAfterRepeatedReads()
    {
        var reminder = CreateReminder(out _);
        var payload = CreatePayload("s8", "Read", new { file_path = "/repo/src/Entities/Order.cs" });

        for (var i = 1; i < HookReminder.EntityReadThreshold; i++)
        {
            Assert.Equal("{}", reminder.Evaluate(payload, HookClient.Claude));
        }

        Assert.Contains("Schema discovery reminder", reminder.Evaluate(payload, HookClient.Claude), StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_PrefersLinqReminderOverEntityReminder()
    {
        var reminder = CreateReminder(out _);
        var read = CreatePayload("s9", "Read", new { file_path = "/repo/src/Entities/Order.cs" });

        for (var i = 0; i < HookReminder.EntityReadThreshold - 1; i++)
        {
            reminder.Evaluate(read, HookClient.Claude);
        }

        // LINQ validation is the primary goal whenever both nudges are pending.
        Assert.Contains("LINQ validation", reminder.Evaluate(LinqWritePayload("s9"), HookClient.Claude), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{"unexpected":true}""")]
    public void Evaluate_NeverThrowsOnBadInput(string? payload)
    {
        var reminder = CreateReminder(out _);
        Assert.Equal("{}", reminder.Evaluate(payload, HookClient.Claude));
    }

    [Fact]
    public void Cleanup_RemovesSessionStateSoCountersRestart()
    {
        var store = new HookSessionStore(_stateDirectory);
        var time = new FakeTimeProvider();
        var reminder = new HookReminder(store, time);

        reminder.Evaluate(LinqWritePayload("s10"), HookClient.Claude);

        Assert.Equal("{}", reminder.Cleanup("""{"session_id":"s10"}"""));

        // State is gone, so the cooldown recorded above no longer suppresses the next reminder.
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.Contains("LINQ validation", reminder.Evaluate(LinqWritePayload("s10"), HookClient.Claude), StringComparison.Ordinal);
    }

    private HookReminder CreateReminder(out FakeTimeProvider time)
    {
        time = new FakeTimeProvider();
        return new HookReminder(new HookSessionStore(_stateDirectory), time);
    }

    private static string LinqWritePayload(string sessionId) => CreatePayload(sessionId, "Edit", new
    {
        file_path = "/repo/src/Orders/OrderQueries.cs",
        new_string = "return await _context.Orders.Where(o => o.Total > 10).ToListAsync();",
    });

    private static string CreatePayload(string sessionId, string toolName, object toolInput) =>
        JsonSerializer.Serialize(new
        {
            hook_event_name = "PostToolUse",
            session_id = sessionId,
            tool_name = toolName,
            tool_input = toolInput,
        });

    public void Dispose()
    {
        GC.SuppressFinalize(this);

        try
        {
            if (Directory.Exists(_stateDirectory))
            {
                Directory.Delete(_stateDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
            // Leftover temp state is harmless.
        }
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan amount) => _now += amount;
    }
}
