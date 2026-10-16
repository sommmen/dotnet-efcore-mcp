using DotnetEfCoreMcp.Server.Updates;

namespace DotnetEfCoreMcp.Server.Tests.Updates;

/// <summary>
/// Covers which published version counts as an upgrade.
/// </summary>
/// <remarks>
/// This is the riskiest logic in the update check because this package publishes previews only. The
/// two failure modes these tests exist to prevent are a preview user being told they are current
/// forever, and a preview user being told to "upgrade" to an older stable release - the latter is
/// easy to write by accident, because SemVer sorts 0.2.0-preview below 0.2.0.
/// </remarks>
public class VersionChannelTests
{
    [Fact]
    public void Offers_a_newer_preview_to_a_preview_user()
    {
        var latest = VersionChannel.SelectUpdate(
            "0.1.32-preview",
            ["0.1.20-preview", "0.1.32-preview", "0.1.39-preview"]);

        Assert.Equal("0.1.39-preview", latest?.ToNormalizedString());
    }

    [Fact]
    public void Never_offers_a_preview_to_a_user_on_a_stable_build()
    {
        // Someone who deliberately installed a stable build should not be nudged onto a preview,
        // even though the preview is numerically newer. That is an upgrade in version and a
        // downgrade in stability.
        var latest = VersionChannel.SelectUpdate("1.0.0", ["1.0.0", "1.1.0-preview", "2.0.0-preview"]);

        Assert.Null(latest);
    }

    [Fact]
    public void Offers_a_newer_stable_release_to_a_stable_user()
    {
        var latest = VersionChannel.SelectUpdate("1.0.0", ["1.0.0", "1.1.0", "1.1.0-preview"]);

        Assert.Equal("1.1.0", latest?.ToNormalizedString());
    }

    [Fact]
    public void Offers_the_stable_release_a_preview_was_leading_up_to()
    {
        // 0.2.0 outranks 0.2.0-preview, so a preview user should be moved onto the release it was
        // previewing once it ships.
        var latest = VersionChannel.SelectUpdate("0.2.0-preview", ["0.2.0-preview", "0.2.0"]);

        Assert.Equal("0.2.0", latest?.ToNormalizedString());
    }

    [Fact]
    public void Never_suggests_a_downgrade_to_an_older_stable_release()
    {
        // The specific trap this guards: a stable 0.1.0 exists while the user runs a newer
        // 0.2.0-preview. Comparing against "the latest stable" without checking the ordering would
        // tell them to move backwards.
        var latest = VersionChannel.SelectUpdate("0.2.0-preview", ["0.1.0", "0.2.0-preview"]);

        Assert.Null(latest);
    }

    [Fact]
    public void Reports_nothing_when_already_on_the_newest_version()
    {
        Assert.Null(VersionChannel.SelectUpdate("0.1.39-preview", ["0.1.20-preview", "0.1.39-preview"]));
    }

    [Fact]
    public void Reports_nothing_when_the_installed_version_is_unparseable()
    {
        // A local F5 build lands here. It must never produce a notice.
        Assert.Null(VersionChannel.SelectUpdate("not-a-version", ["1.0.0"]));
        Assert.Null(VersionChannel.SelectUpdate(null, ["1.0.0"]));
        Assert.Null(VersionChannel.SelectUpdate(string.Empty, ["1.0.0"]));
    }

    [Fact]
    public void Reports_nothing_when_the_feed_lists_no_versions()
    {
        Assert.Null(VersionChannel.SelectUpdate("0.1.32-preview", []));
    }

    [Fact]
    public void Skips_versions_it_cannot_parse_rather_than_failing()
    {
        // Feeds can list things this client does not understand. One bad entry must not discard
        // the rest of the answer.
        var latest = VersionChannel.SelectUpdate(
            "0.1.32-preview",
            ["garbage", "0.1.39-preview", string.Empty]);

        Assert.Equal("0.1.39-preview", latest?.ToNormalizedString());
    }

    [Fact]
    public void Picks_the_newest_candidate_regardless_of_feed_ordering()
    {
        var latest = VersionChannel.SelectUpdate(
            "0.1.20-preview",
            ["0.1.39-preview", "0.1.22-preview", "0.1.32-preview"]);

        Assert.Equal("0.1.39-preview", latest?.ToNormalizedString());
    }

    [Fact]
    public void Compares_build_suffixed_preview_versions_correctly()
    {
        // Nerdbank.GitVersioning emits versions like 0.1.40-preview.g4571fad801, which must still
        // order sensibly against the plain preview versions published alongside them.
        var latest = VersionChannel.SelectUpdate(
            "0.1.32-preview.g842ad5eb9d",
            ["0.1.32-preview.g842ad5eb9d", "0.1.39-preview"]);

        Assert.Equal("0.1.39-preview", latest?.ToNormalizedString());
    }

    [Theory]
    [InlineData("0.1.39-preview", true)]
    [InlineData("0.1.40-preview.g4571fad801", true)]
    [InlineData("1.0.0", false)]
    [InlineData("not-a-version", false)]
    [InlineData(null, false)]
    public void Classifies_the_channel_from_the_version(string? version, bool expected)
    {
        Assert.Equal(expected, VersionChannel.IsPrerelease(version));
    }

    [Fact]
    public void Checks_preview_builds_more_often_than_stable_ones()
    {
        // Previews ship far more often, so a weekly check would usually be reporting stale news.
        Assert.Equal(TimeSpan.FromHours(24), VersionChannel.GetCheckInterval("0.1.39-preview"));
        Assert.Equal(TimeSpan.FromDays(7), VersionChannel.GetCheckInterval("1.0.0"));
    }
}
