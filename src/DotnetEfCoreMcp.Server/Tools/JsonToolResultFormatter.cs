using System.Text.Json;
using System.Text.Json.Serialization;

namespace DotnetEfCoreMcp.Server.Tools;

public sealed class JsonToolResultFormatter : IToolResultFormatter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string Format(object value) => JsonSerializer.Serialize(value, JsonOptions);
}
