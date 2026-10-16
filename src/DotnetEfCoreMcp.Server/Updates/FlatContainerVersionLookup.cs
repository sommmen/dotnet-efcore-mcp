using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace DotnetEfCoreMcp.Server.Updates;

/// <summary>
/// Reads published versions from a NuGet V3 feed's flat container (package base address).
/// </summary>
/// <remarks>
/// <para>
/// Deliberately a plain <see cref="HttpClient"/> GET rather than the <c>NuGet.Protocol</c> client.
/// The flat container's version index is a single unauthenticated JSON document - a string array of
/// versions - and taking the full NuGet client stack to read it would add a large dependency to a
/// cosmetic feature.
/// </para>
/// <para>
/// The consequence, documented for users, is that this queries one feed and does not honor
/// <c>NuGet.Config</c>, source mapping, or credential providers. A user on a private mirror can
/// turn the check off; see the opt-outs in <see cref="UpdateCheckGate"/>.
/// </para>
/// </remarks>
public sealed partial class FlatContainerVersionLookup : INuGetVersionLookup
{
    /// <summary>Default flat container base address for nuget.org.</summary>
    public const string DefaultBaseAddress = "https://api.nuget.org/v3-flatcontainer/";

    private readonly HttpClient _httpClient;
    private readonly Uri? _baseAddress;

    /// <summary>
    /// Creates a lookup against a feed's flat container.
    /// </summary>
    /// <param name="httpClient">Client used for the request.</param>
    /// <param name="baseAddress">
    /// Flat container base address, defaulting to nuget.org. Overridable so a user on a mirror can
    /// point the check at a feed they can actually install from.
    /// </param>
    public FlatContainerVersionLookup(HttpClient httpClient, string? baseAddress = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);

        _httpClient = httpClient;
        var address = string.IsNullOrWhiteSpace(baseAddress) ? DefaultBaseAddress : EnsureTrailingSlash(baseAddress);
        _baseAddress = Uri.TryCreate(address, UriKind.Absolute, out var parsedAddress) ? parsedAddress : null;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> GetVersionsAsync(
        string packageId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);

        if (_baseAddress is null)
        {
            return [];
        }

        // The flat container addresses packages by lowercase id.
        var requestUri = new Uri(_baseAddress, $"{Uri.EscapeDataString(packageId.ToLowerInvariant())}/index.json");

        try
        {
            var index = await _httpClient
                .GetFromJsonAsync(requestUri, FlatContainerJsonContext.Default.FlatContainerIndex, cancellationToken)
                .ConfigureAwait(false);

            return index?.Versions ?? [];
        }
        catch (Exception exception) when (exception is HttpRequestException
                                              or TaskCanceledException
                                              or OperationCanceledException
                                              or System.Text.Json.JsonException
                                              or UriFormatException)
        {
            // Offline, proxied, rate-limited, air-gapped, slow, or serving something unexpected.
            // All of these are ordinary and none of them is worth telling anyone about: "no result"
            // is a valid outcome for an update check, and the caller treats it as "do not notify".
            return [];
        }
    }

    private static string EnsureTrailingSlash(string value) =>
        value.EndsWith('/') ? value : value + "/";

    /// <summary>Shape of the flat container's version index document.</summary>
    internal sealed class FlatContainerIndex
    {
        /// <summary>Published versions, including prereleases and unlisted ones.</summary>
        [JsonPropertyName("versions")]
        public IReadOnlyList<string> Versions { get; set; } = [];
    }

    [JsonSerializable(typeof(FlatContainerIndex))]
    internal sealed partial class FlatContainerJsonContext : JsonSerializerContext;
}
