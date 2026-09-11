using System.Text.Json;
using System.Text.Json.Serialization;
using ToonFormat;

namespace DotnetEfCoreMcp.Server.Tools;

public sealed class ToonToolResultFormatter : IToolResultFormatter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // Serialize with our own JsonSerializerOptions first (preserving cycle handling and
    // null-omission), then hand the resulting JSON off to Toon.DotNet for TOON conversion;
    // Toon.Encode(object, ...) would otherwise re-serialize with its own fixed options.
    public string Format(object value) => Toon.FromJson(JsonSerializer.Serialize(value, JsonOptions));
}
