namespace DotnetEfCoreMcp.Server.Updates;

/// <summary>
/// Checks whether a newer version of this server has been published.
/// </summary>
/// <remarks>
/// <para>
/// Notify only. This never installs anything, for reasons that compound: the MCP client owns this
/// process and chose its version, so reinstalling underneath it is both hostile and racy; a stdio
/// server has no terminal to confirm with; and updating a global tool while hook-invoked processes
/// of the pinned version may be running concurrently is an unforced hazard.
/// </para>
/// <para>
/// Every failure path is silent. Offline, proxied, rate-limited, slow, or air-gapped all produce
/// "no update found", which is indistinguishable to callers from "already current" - and that is
/// correct, because in neither case is there anything useful to say.
/// </para>
/// </remarks>
public sealed class UpdateChecker
{
    /// <summary>NuGet package id to check.</summary>
    public const string PackageId = "DotnetEfCoreMcp.Server";

    /// <summary>
    /// Overall budget for a check, including the cache read and the HTTP request.
    /// </summary>
    /// <remarks>
    /// Modeled on HashiCorp Checkpoint's 3s HTTP timeout, with headroom. Their comment is the whole
    /// rationale: the result is "not critical enough to block on if [the service] is broken/slow."
    /// </remarks>
    public static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);

    private readonly INuGetVersionLookup _lookup;
    private readonly UpdateCheckCache _cache;
    private readonly UpdateCheckGate _gate;
    private readonly InstallMethodDetector _detector;
    private readonly string? _installedVersion;

    /// <summary>
    /// Creates a checker for the running build.
    /// </summary>
    /// <param name="lookup">Feed lookup.</param>
    /// <param name="cache">Throttling cache.</param>
    /// <param name="gate">Decides whether checking is permitted at all.</param>
    /// <param name="detector">Install method detection.</param>
    public UpdateChecker(
        INuGetVersionLookup lookup,
        UpdateCheckCache cache,
        UpdateCheckGate gate,
        InstallMethodDetector detector)
        : this(lookup, cache, gate, detector, PackageVersionProvider.GetPackageVersion())
    {
    }

    /// <summary>
    /// Creates a checker for an explicit installed version.
    /// </summary>
    /// <param name="lookup">Feed lookup.</param>
    /// <param name="cache">Throttling cache.</param>
    /// <param name="gate">Decides whether checking is permitted at all.</param>
    /// <param name="detector">Install method detection.</param>
    /// <param name="installedVersion">
    /// The running version, or <see langword="null"/> to represent a build carrying no packaging
    /// metadata. This is a separate constructor rather than an optional parameter precisely so that
    /// null means "no version" and cannot be confused with "use the default".
    /// </param>
    internal UpdateChecker(
        INuGetVersionLookup lookup,
        UpdateCheckCache cache,
        UpdateCheckGate gate,
        InstallMethodDetector detector,
        string? installedVersion)
    {
        ArgumentNullException.ThrowIfNull(lookup);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(detector);

        _lookup = lookup;
        _cache = cache;
        _gate = gate;
        _detector = detector;
        _installedVersion = installedVersion;
    }

    /// <summary>
    /// Runs a check, using the cached result when it is still fresh.
    /// </summary>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns>
    /// The outcome. Never throws for an ordinary failure; an unreachable feed yields a result with
    /// no newer version rather than an exception.
    /// </returns>
    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        var method = _detector.Detect();

        if (!_gate.IsAllowed(_installedVersion, method))
        {
            return UpdateCheckResult.NotChecked(_installedVersion, method);
        }

        var cached = _cache.Read();

        if (_cache.IsFresh(cached, _installedVersion))
        {
            return BuildResult(cached!.LatestVersion, method);
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(Budget);

            var versions = await _lookup.GetVersionsAsync(PackageId, timeout.Token).ConfigureAwait(false);

            // An empty list means the feed was unreachable or had nothing to say. Not caching that
            // is deliberate: caching a failure would suppress checks for a full day because the
            // network happened to be down for a moment.
            if (versions.Count == 0)
            {
                return UpdateCheckResult.NotChecked(_installedVersion, method);
            }

            var latest = VersionChannel.SelectUpdate(_installedVersion, versions)?.ToNormalizedString();

            _cache.Write(_installedVersion, latest);

            return BuildResult(latest, method);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Last-resort guard. Nothing about an update check justifies propagating a failure into
            // server startup or a tool call, so this swallows anything the lookup did not.
            return UpdateCheckResult.NotChecked(_installedVersion, method);
        }
    }

    private UpdateCheckResult BuildResult(string? latestVersion, InstallMethod method)
    {
        var upgradeCommand = latestVersion is null
            ? null
            : InstallMethodDetector.GetUpgradeCommand(method, VersionChannel.IsPrerelease(latestVersion));

        return new UpdateCheckResult(_installedVersion, latestVersion, method, upgradeCommand, Checked: true);
    }
}
