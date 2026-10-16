using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DotnetEfCoreMcp.Server.Updates;

/// <summary>
/// Runs the update check once in the background and holds the result for the <c>onboard</c> tool.
/// </summary>
/// <remarks>
/// <para>
/// Runs as a hosted service, deliberately started after the host is built and never on the
/// <c>initialize</c> path, so a slow or unreachable feed cannot delay the MCP handshake. Server
/// startup must not depend on the network under any circumstances.
/// </para>
/// <para>
/// Delivery is deliberately passive. The result is reported when the agent asks for it through the
/// <c>onboard</c> tool, and logged once for a human watching the server's own output. It is never
/// appended to query or schema results: that would inflate every response with text the model did
/// not ask for, risk it treating an infrastructure notice as part of the task, and corrupt the
/// machine-consumed payloads the tool result formatters produce.
/// </para>
/// </remarks>
public sealed class UpdateNotificationService(
    UpdateChecker checker,
    ILogger<UpdateNotificationService> logger) : BackgroundService
{
    private UpdateCheckResult? _result;

    /// <summary>
    /// The outcome of the check, or <see langword="null"/> while it is still running.
    /// </summary>
    /// <remarks>
    /// Null is meaningfully different from a result reporting no update: it means nobody has
    /// looked yet, which callers report as "not checked" rather than "up to date".
    /// </remarks>
    public UpdateCheckResult? Result => _result;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Yield first so the host finishes starting before any network work begins. Without
            // this, a hosted service's synchronous prologue runs inline during StartAsync.
            await Task.Yield();

            var result = await checker.CheckAsync(stoppingToken).ConfigureAwait(false);
            _result = result;

            if (result.Notice is not { } notice)
            {
                return;
            }

            // The logger writes to stderr, which the stdio transport permits; stdout carries
            // nothing but JSON-RPC. Logged once, at the level the MCP logging spec reserves for
            // "normal but significant".
            logger.LogInformation("{Notice}", notice);
        }
        catch (OperationCanceledException)
        {
            // The server is shutting down mid-check. Nothing to report and nothing wrong.
        }
        catch (Exception exception)
        {
            // UpdateChecker already swallows ordinary failures, so reaching here means something
            // unanticipated. It still must not escape: an unobserved exception from a background
            // service can take the host down, and no update check is worth that.
            logger.LogDebug(exception, "Update check failed.");
        }
    }
}
