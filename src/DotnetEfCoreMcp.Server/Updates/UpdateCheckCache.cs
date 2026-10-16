using System.Text.Json;
using System.Text.Json.Serialization;
using DotnetEfCoreMcp.Server.Onboarding;

namespace DotnetEfCoreMcp.Server.Updates;

/// <summary>
/// Throttles update checks by remembering the last result on disk.
/// </summary>
/// <remarks>
/// <para>
/// Without this, every server start would hit the network - and an MCP server starts far more often
/// than a CLI is invoked, because clients launch one per session. The cache turns that into at most
/// one request per day on a preview build.
/// </para>
/// <para>
/// Every operation is best-effort. A missing, unreadable, or corrupt cache file is a cache miss, not
/// an error: this is a cosmetic feature and must never be able to affect startup.
/// </para>
/// </remarks>
public sealed class UpdateCheckCache
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };

    private readonly string _filePath;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Creates a cache at the default per-user location.
    /// </summary>
    public UpdateCheckCache()
        : this(GetDefaultPath(), TimeProvider.System)
    {
    }

    /// <summary>
    /// Creates a cache at an explicit path, so tests stay out of the real user profile.
    /// </summary>
    /// <param name="filePath">Path of the cache file.</param>
    /// <param name="timeProvider">Clock used for freshness checks.</param>
    internal UpdateCheckCache(string filePath, TimeProvider timeProvider)
    {
        _filePath = filePath;
        _timeProvider = timeProvider;
    }

    /// <summary>Path of the cache file.</summary>
    public string FilePath => _filePath;

    /// <summary>
    /// Returns the default cache path for the current user.
    /// </summary>
    /// <remarks>
    /// Uses the same <see cref="UserDirectory"/> resolution the rest of this server's user-scoped
    /// state uses, so the <c>DOTNETEFCOREMCP_HOMEDIR</c> override that keeps tests out of the real
    /// profile applies here too.
    /// </remarks>
    public static string GetDefaultPath() =>
        Path.Combine(UserDirectory.Resolve(), ".dotnet-efcore-mcp", "update-check.json");

    /// <summary>
    /// Reads the cached result, if one is present and readable.
    /// </summary>
    /// <returns>The cached entry, or <see langword="null"/> on any miss.</returns>
    public UpdateCheckCacheEntry? Read()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return null;
            }

            return JsonSerializer.Deserialize<UpdateCheckCacheEntry>(File.ReadAllText(_filePath));
        }
        catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException
                                              or JsonException
                                              or ArgumentException
                                              or NotSupportedException)
        {
            // Corrupt or unreadable cache behaves exactly like no cache: check again.
            return null;
        }
    }

    /// <summary>
    /// Reports whether a cached entry is still fresh enough to use instead of a network call.
    /// </summary>
    /// <param name="entry">The cached entry, or null.</param>
    /// <param name="installedVersion">The running version.</param>
    /// <remarks>
    /// A cache recorded against a different installed version is always stale - the user upgraded,
    /// so the previous answer is about a version they no longer run.
    /// </remarks>
    public bool IsFresh(UpdateCheckCacheEntry? entry, string? installedVersion)
    {
        if (entry is null)
        {
            return false;
        }

        if (!string.Equals(entry.InstalledVersion, installedVersion, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var age = _timeProvider.GetUtcNow() - entry.CheckedAt;

        // A negative age means the clock moved backwards (or the file was written by a machine with
        // a different clock). Treat it as stale rather than trusting it indefinitely.
        return age >= TimeSpan.Zero && age < VersionChannel.GetCheckInterval(installedVersion);
    }

    /// <summary>
    /// Records the outcome of a check.
    /// </summary>
    /// <param name="installedVersion">Version that was running when the check ran.</param>
    /// <param name="latestVersion">Newest version found, or null when none was newer.</param>
    public void Write(string? installedVersion, string? latestVersion)
    {
        var entry = new UpdateCheckCacheEntry
        {
            CheckedAt = _timeProvider.GetUtcNow(),
            InstalledVersion = installedVersion,
            LatestVersion = latestVersion,
        };

        string? temporaryPath = null;

        try
        {
            var fullPath = Path.GetFullPath(_filePath);
            var directory = Path.GetDirectoryName(fullPath)!;
            Directory.CreateDirectory(directory);

            temporaryPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(entry, SerializerOptions));
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException
                                              or NotSupportedException
                                              or ArgumentException)
        {
            // A read-only or sandboxed home directory means the check simply runs again next time.
            // That is a performance cost, not a correctness problem, and never worth an error.
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // The temporary file is harmless and will be ignored by future reads.
                }
            }
        }
    }
}

/// <summary>
/// What a previous update check found.
/// </summary>
public sealed class UpdateCheckCacheEntry
{
    /// <summary>When the check ran.</summary>
    [JsonPropertyName("checkedAt")]
    public DateTimeOffset CheckedAt { get; set; }

    /// <summary>Version that was installed when the check ran.</summary>
    [JsonPropertyName("installedVersion")]
    public string? InstalledVersion { get; set; }

    /// <summary>Newest version found, or null when nothing newer was available.</summary>
    [JsonPropertyName("latestVersion")]
    public string? LatestVersion { get; set; }
}
