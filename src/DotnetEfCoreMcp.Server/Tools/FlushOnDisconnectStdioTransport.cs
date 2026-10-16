using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DotnetEfCoreMcp.Server.Tools;

/// <summary>The stdio transport the server runs on: a <see cref="StreamServerTransport"/> whose send
/// path keeps writing to stdout after the client has closed stdin.
/// <para>The SDK's stream transport marks itself disconnected as soon as its input stream reports
/// end-of-stream, and its inherited send path drops messages once disconnected. A client that
/// writes a request and immediately closes stdin - which is what a scripted or short-lived session
/// does - therefore races the handler: stdin reaches end-of-stream first, the transport
/// disconnects, and the response the handler then produces is discarded instead of being written to
/// stdout. The symptom is a session that produces no output at all.</para>
/// <para>Closing stdin only says the client will send nothing further; it does not mean the client
/// has stopped reading, so overriding the send path to write regardless of connection state is what
/// lets an in-flight response still reach the client. Writes are serialized and the stream's own
/// disposal is tolerated, so a send that genuinely arrives after shutdown is dropped quietly rather
/// than faulting host shutdown.</para></summary>
internal sealed class FlushOnDisconnectStdioTransport : StreamServerTransport
{
    private static readonly byte[] NewLine = "\n"u8.ToArray();

    private readonly Stream _output;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly ILogger? _logger;
    private int _disposed;

    public FlushOnDisconnectStdioTransport(Stream input, Stream output, string serverName, ILoggerFactory? loggerFactory = null)
        : base(input, output, serverName, loggerFactory)
    {
        _output = output;
        _logger = loggerFactory?.CreateLogger<FlushOnDisconnectStdioTransport>();
    }

    public override async Task SendMessageAsync(JsonRpcMessage message, CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            _logger?.LogDebug("Dropped an MCP message because the transport was already disposed.");
            return;
        }

        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var json = JsonSerializer.SerializeToUtf8Bytes(
                message,
                McpJsonUtilities.DefaultOptions.GetTypeInfo(typeof(JsonRpcMessage)));

            await _output.WriteAsync(json, cancellationToken).ConfigureAwait(false);
            await _output.WriteAsync(NewLine, cancellationToken).ConfigureAwait(false);
            await _output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or IOException or NotSupportedException)
        {
            // stdout is already gone (the transport has been disposed, or the client's pipe was
            // closed). There is nowhere left to report this, and throwing would fault host
            // shutdown, so drop the message.
            _logger?.LogDebug(ex, "Dropped an MCP message because standard output was already closed.");
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        // Drain any send already in flight before tearing the lock down, so a response being
        // written as shutdown begins still completes rather than being cut off.
        await _sendLock.WaitAsync().ConfigureAwait(false);
        _sendLock.Release();

        await base.DisposeAsync().ConfigureAwait(false);
    }
}
