using Cysharp.AI;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DotnetEfCoreMcp.Server.Tools;

public sealed class ToonToolResultFormatter : IToolResultFormatter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string Format(object value) => ToonEncoder.Encode(value, JsonOptions);
}
