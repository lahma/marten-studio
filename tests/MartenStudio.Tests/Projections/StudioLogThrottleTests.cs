using System.Globalization;
using System.Net.Sockets;

using MartenStudio.Services;
using MartenStudio.Tests.Support;

using Microsoft.Extensions.Logging;

using Npgsql;

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
    private const string Invalid = "System.InvalidOperationException";

    private static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_first_occurrence_is_a_Warning_and_the_rest_of_the_window_is_Debug()
    {
        var clock = new FakeTimeProvider(Now);
        var throttle = new StudioLogThrottle(clock);

        throttle.WarningOrDebug("site", "default", "db", Invalid).Should().Be(LogLevel.Warning);

        for (int poll = 0; poll < 119; poll++)
        {
            clock.Advance(TimeSpan.FromSeconds(5));
            throttle.ShouldLog("site", "default", "db", Invalid).Should().BeFalse(
                "poll {0} is still inside the window", poll);
        }

        // 119 polls of five seconds is 595 s; one more five seconds is the window exactly.
        clock.Advance(TimeSpan.FromSeconds(5));
        throttle.ShouldLog("site", "default", "db", Invalid).Should().BeTrue();
    }

    [Theory]
    [InlineData("other-site", "default", "db", Invalid)]
    [InlineData("site", "other-store", "db", Invalid)]
    [InlineData("site", "default", "other-db", Invalid)]
    [InlineData("site", "default", "db", "System.TimeoutException")]
    [InlineData("site", null, "db", Invalid)]
    [InlineData("site", "default", null, Invalid)]
    [InlineData("site", "default", "db", null)]
    public void A_different_site_store_database_or_kind_is_told_about_in_its_own_right(
        string site, string? storeKey, string? databaseId, string? kind)
    {
        var throttle = new StudioLogThrottle(new FakeTimeProvider(Now));

        throttle.ShouldLog("site", "default", "db", Invalid).Should().BeTrue();
        throttle.ShouldLog(site, storeKey, databaseId, kind).Should().BeTrue();
    }

    [Fact]
    public void Store_and_database_keys_are_compared_the_way_the_studio_compares_them()
    {
        var throttle = new StudioLogThrottle(new FakeTimeProvider(Now));

        throttle.ShouldLog("site", "Default", "DB", Invalid).Should().BeTrue();
        throttle.ShouldLog("site", "default", "db", Invalid).Should().BeFalse(
            "a store key from a URL and one from the registration are the same store");
    }

    // ------------------------------------------------------------------------------------------------
    // The kind (DB-0-fix, item 3)
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// The finding: keyed on the exception type, every Postgres error is one key, so a role that lost a
    /// grant silenced the pool running out of connections and a deadlock for ten minutes.
    /// </summary>
    [Fact]
    public void Three_different_Postgres_errors_are_three_warnings_and_not_one()
    {
        var throttle = new StudioLogThrottle(new FakeTimeProvider(Now));

        foreach (string sqlState in new[] { "42501", "53300", "40P01" })
        {
            throttle.WarningOrDebug("site", "default", "db", StudioLogThrottle.KindOf(Postgres(sqlState)))
                .Should().Be(LogLevel.Warning, "{0} is a different problem from the ones before it", sqlState);
        }

        throttle.WarningOrDebug("site", "default", "db", StudioLogThrottle.KindOf(Postgres("53300")))
            .Should().Be(LogLevel.Debug, "the same SQLSTATE again is the same problem again");
    }

    [Fact]
    public void The_kind_of_a_Postgres_error_is_its_type_and_its_SQLSTATE()
    {
        StudioLogThrottle.KindOf(Postgres("42501")).Should().Be("Npgsql.PostgresException:42501");
    }

    /// <summary>
    /// A timeout, a refused socket and a pool that ran dry all arrive as <see cref="NpgsqlException" />, and
    /// what it wraps is what tells them apart.
    /// </summary>
    [Fact]
    public void The_kind_of_an_NpgsqlException_names_what_it_wraps()
    {
        string timeout = StudioLogThrottle.KindOf(new NpgsqlException("Exception while reading from stream", new TimeoutException()));
        string socket = StudioLogThrottle.KindOf(new NpgsqlException("Failed to connect to x", new SocketException()));

        timeout.Should().Be("Npgsql.NpgsqlException(System.TimeoutException)");
        socket.Should().Be("Npgsql.NpgsqlException(System.Net.Sockets.SocketException)");

        var throttle = new StudioLogThrottle(new FakeTimeProvider(Now));
        throttle.ShouldLog("site", "default", "db", timeout).Should().BeTrue();
        throttle.ShouldLog("site", "default", "db", socket).Should().BeTrue("a socket failure is not the timeout before it");
    }

    [Fact]
    public void The_kind_of_anything_else_is_its_type_and_nothing_from_its_message()
    {
        StudioLogThrottle.KindOf(new InvalidOperationException("tenant acme, row 42")).Should().Be(Invalid);
        StudioLogThrottle.KindOf(new NpgsqlException("no inner")).Should().Be("Npgsql.NpgsqlException");
    }

    /// <summary>
    /// A caller that wraps a Postgres error - Marten's <c>MartenCommandException</c> is the usual one -
    /// keeps the SQLSTATE in the key, or every wrapped error would be one key again.
    /// </summary>
    [Fact]
    public void A_wrapped_Postgres_error_still_keys_on_its_SQLSTATE()
    {
        var wrapped = new InvalidOperationException("command failed", new InvalidOperationException("inner", Postgres("42501")));

        StudioLogThrottle.KindOf(wrapped).Should().Be(Invalid + ":42501");
        StudioLogThrottle.KindOf(new InvalidOperationException("command failed", Postgres("53300")))
            .Should().NotBe(StudioLogThrottle.KindOf(wrapped));
    }

    /// <summary>
    /// The one helper a throttled site may use besides the throttle itself, for owners a test builds
    /// without one: the throttle's answer when there is one, a Warning when there is not.
    /// </summary>
    [Fact]
    public void LevelOrWarning_is_the_throttle_when_there_is_one_and_Warning_when_there_is_not()
    {
        StudioLogThrottle.LevelOrWarning(null, "site", "default", "db", Invalid).Should().Be(LogLevel.Warning);
        StudioLogThrottle.LevelOrWarning(null, "site", "default", "db", Invalid).Should().Be(LogLevel.Warning);

        var throttle = new StudioLogThrottle(new FakeTimeProvider(Now));
        StudioLogThrottle.LevelOrWarning(throttle, "site", "default", "db", Invalid).Should().Be(LogLevel.Warning);
        StudioLogThrottle.LevelOrWarning(throttle, "site", "default", "db", Invalid).Should().Be(LogLevel.Debug);
    }

    // ------------------------------------------------------------------------------------------------
    // Bounds
    // ------------------------------------------------------------------------------------------------

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

    private static PostgresException Postgres(string sqlState) =>
        new("something went wrong", "ERROR", "ERROR", sqlState);
}
