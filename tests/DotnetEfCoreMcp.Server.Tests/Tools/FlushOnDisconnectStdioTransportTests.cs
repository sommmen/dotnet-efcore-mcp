using System.Text;
using System.Text.Json;
using DotnetEfCoreMcp.Server.Tools;
using ModelContextProtocol.Protocol;

namespace DotnetEfCoreMcp.Server.Tests.Tools;

/// <summary>Covers the stdio transport's send path after the client has closed stdin. The SDK's
/// stream transport marks itself disconnected the moment its input stream reports end-of-stream and
/// then silently discards every later message, so a request still being handled when stdin closes
/// loses its response entirely.</summary>
public sealed class FlushOnDisconnectStdioTransportTests
{
    [Fact]
    public async Task SendMessageAsync_WritesResponse_AfterInputReachedEndOfStream()
    {
        var output = new MemoryStream();
        await using var transport = await CreateTransportAtEndOfStreamAsync(output);

        await transport.SendMessageAsync(
            new JsonRpcResponse { Id = new RequestId(1), Result = default },
            TestContext.Current.CancellationToken);

        var written = JsonDocument.Parse(ReadLines(output).Single());
        Assert.Equal(1, written.RootElement.GetProperty("id").GetInt32());
    }

    [Fact]
    public async Task SendMessageAsync_WritesOneNewlineDelimitedMessagePerCall()
    {
        var output = new MemoryStream();
        await using var transport = await CreateTransportAtEndOfStreamAsync(output);

        await transport.SendMessageAsync(
            new JsonRpcResponse { Id = new RequestId(1), Result = default },
            TestContext.Current.CancellationToken);
        await transport.SendMessageAsync(
            new JsonRpcResponse { Id = new RequestId(2), Result = default },
            TestContext.Current.CancellationToken);

        var ids = ReadLines(output)
            .Select(line => JsonDocument.Parse(line).RootElement.GetProperty("id").GetInt32())
            .ToArray();

        Assert.Equal([1, 2], ids);
    }

    [Fact]
    public async Task SendMessageAsync_DoesNotThrow_AfterTheOutputStreamIsClosed()
    {
        var output = new MemoryStream();
        var transport = await CreateTransportAtEndOfStreamAsync(output);
        await transport.DisposeAsync();

        await transport.SendMessageAsync(
            new JsonRpcResponse { Id = new RequestId(1), Result = default },
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task SendMessageAsync_DoesNotThrow_WhenOutputStreamIsClosedWithoutDisposingTransport()
    {
        var output = new MemoryStream();
        await using var transport = await CreateTransportAtEndOfStreamAsync(output);
        await output.DisposeAsync();

        await transport.SendMessageAsync(
            new JsonRpcResponse { Id = new RequestId(1), Result = default },
            TestContext.Current.CancellationToken);
    }

    private static async Task<FlushOnDisconnectStdioTransport> CreateTransportAtEndOfStreamAsync(Stream output)
    {
        var input = new MemoryStream("""{"jsonrpc":"2.0","id":1,"method":"ping"}"""u8.ToArray());
        var transport = new FlushOnDisconnectStdioTransport(input, output, "test");

        // Drain the single request so the transport's read loop runs on to end-of-stream, which is
        // what makes it consider itself disconnected.
        await foreach (var _ in transport.MessageReader.ReadAllAsync(TestContext.Current.CancellationToken))
        {
            break;
        }

        while (transport.IsConnected)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        return transport;
    }

    private static string[] ReadLines(MemoryStream output) => Encoding.UTF8
        .GetString(output.ToArray())
        .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
