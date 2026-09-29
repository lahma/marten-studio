using System.Globalization;

using MartenStudio.Services;
using MartenStudio.Tests.Support;

using Microsoft.Extensions.Logging;

namespace MartenStudio.Tests.Projections;

/// <summary>
/// The rule a polled anomaly is logged by: Warning the first time per key per ten minutes, Debug after.
/// </summary>
/// <remarks>
/// Filed beside the projections tests because the daemon card is the path it was written for - the
/// production report was a warning every <c>RefreshInterval</c> from the Overview's daemon tile - though
/// every polled read in the studio goes through it.
/// </remarks>
public class StudioLogThrottleTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_first_occurrence_is_a_Warning_and_the_rest_of_the_window_is_Debug()
    {
        var clock = new FakeTimeProvider(Now);
        var throttle = new StudioLogThrottle(clock);

        throttle.WarningOrDebug("site", "default", "db", typeof(InvalidOperationException)).Should().Be(LogLevel.Warning);

        for (int poll = 0; poll < 119; poll++)
        {
            clock.Advance(TimeSpan.FromSeconds(5));
            throttle.ShouldLog("site", "default", "db", typeof(InvalidOperationException)).Should().BeFalse(
                "poll {0} is still inside the window", poll);
        }

        // 119 polls of five seconds is 595 s; one more five seconds is the window exactly.
        clock.Advance(TimeSpan.FromSeconds(5));
        throttle.ShouldLog("site", "default", "db", typeof(InvalidOperationException)).Should().BeTrue();
    }

    [Theory]
    [InlineData("other-site", "default", "db", typeof(InvalidOperationException))]
    [InlineData("site", "other-store", "db", typeof(InvalidOperationException))]
    [InlineData("site", "default", "other-db", typeof(InvalidOperationException))]
    [InlineData("site", "default", "db", typeof(TimeoutException))]
    [InlineData("site", null, "db", typeof(InvalidOperationException))]
    [InlineData("site", "default", null, typeof(InvalidOperationException))]
    [InlineData("site", "default", "db", null)]
    public void A_different_site_store_database_or_exception_type_is_told_about_in_its_own_right(
        string site, string? storeKey, string? databaseId, Type? exceptionType)
    {
        var throttle = new StudioLogThrottle(new FakeTimeProvider(Now));

        throttle.ShouldLog("site", "default", "db", typeof(InvalidOperationException)).Should().BeTrue();
        throttle.ShouldLog(site, storeKey, databaseId, exceptionType).Should().BeTrue();
    }

    [Fact]
    public void Store_and_database_keys_are_compared_the_way_the_studio_compares_them()
    {
        var throttle = new StudioLogThrottle(new FakeTimeProvider(Now));

        throttle.ShouldLog("site", "Default", "DB", typeof(InvalidOperationException)).Should().BeTrue();
        throttle.ShouldLog("site", "default", "db", typeof(InvalidOperationException)).Should().BeFalse(
            "a store key from a URL and one from the registration are the same store");
    }

    /// <summary>
    /// A singleton that grew one entry per distinct key for the life of the process would be a leak in
    /// the component that exists because something is going wrong.
    /// </summary>
    [Fact]
    public void The_key_set_is_bounded_and_a_new_key_still_logs_when_it_is_full()
    {
        var clock = new FakeTimeProvider(Now);
        var throttle = new StudioLogThrottle(clock);

        for (int key = 0; key < StudioLogThrottle.MaxKeys; key++)
        {
            throttle.ShouldLog("site", Key(key), null, null).Should().BeTrue();
            clock.Advance(TimeSpan.FromMilliseconds(1));
        }

        throttle.Count.Should().Be(StudioLogThrottle.MaxKeys);

        // Full, and nothing has expired: the oldest key makes room, and the new one is still told about.
        throttle.ShouldLog("site", "one more", null, null).Should().BeTrue();
        throttle.Count.Should().Be(StudioLogThrottle.MaxKeys);
        throttle.ShouldLog("site", Key(0), null, null).Should().BeTrue("the oldest key was the one forgotten");

        // Once the window has passed, a full map sheds every expired key at once.
        clock.Advance(StudioLogThrottle.Window);
        throttle.ShouldLog("site", "after the window", null, null).Should().BeTrue();
        throttle.Count.Should().BeLessThan(StudioLogThrottle.MaxKeys);
    }

    [Fact]
    public void A_site_is_required()
    {
        var throttle = new StudioLogThrottle();

        Action blank = () => throttle.ShouldLog(" ", null, null, null);

        blank.Should().Throw<ArgumentException>();
    }

    private static string Key(int value) => value.ToString(CultureInfo.InvariantCulture);
}
