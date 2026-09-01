namespace DotnetEfCoreMcp.Server.Tools;

/// <summary>Formats successful MCP tool result content independently of the MCP JSON-RPC transport.</summary>
public interface IToolResultFormatter
{
    string Format(object value);
}
