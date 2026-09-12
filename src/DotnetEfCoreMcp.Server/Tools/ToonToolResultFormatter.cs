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

    // Serialize to a JsonElement with our own JsonSerializerOptions first (preserving cycle
    // handling and null-omission), then hand that element to Toon.Encode. Toon.DotNet's
    // Normalizer passes a JsonElement input through unchanged, so this avoids the extra
    // string allocation and reparse that Toon.FromJson(JsonSerializer.Serialize(...)) would
    // incur, while still avoiding Toon.Encode's fixed JSON options for non-JsonElement input.
    public string Format(object value) => Toon.Encode(JsonSerializer.SerializeToElement(value, JsonOptions));
}
