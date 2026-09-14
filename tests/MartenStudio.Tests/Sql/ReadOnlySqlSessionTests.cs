using MartenStudio.Internal.Sql;

using Npgsql;

namespace MartenStudio.Tests.Sql;

/// <summary>
/// The parts of the read-only session that need no database. Everything else about it — that the
/// transaction refuses writes, that the timeouts fire, that the row cap stops the reader — is asserted
/// against a real Postgres in the integration suite, because that is the only place those claims mean
/// anything.
/// </summary>
public class ReadOnlySqlSessionTests
{
    [Theory]
    [InlineData("readonly_role")]
    [InlineData("_private")]
    [InlineData("app$worker")]
    [InlineData("Role1")]
    public void A_plain_identifier_is_a_role_the_session_will_switch_to(string role) =>
        ReadOnlySqlSession.IsValidRoleName(role).Should().BeTrue();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1role")]
    [InlineData("has space")]
    [InlineData("has\"quote")]
    [InlineData("has;semicolon")]
    [InlineData("postgres; drop table x")]
    [InlineData("has-dash")]
    public void Anything_that_is_not_a_plain_identifier_is_refused(string? role) =>
        ReadOnlySqlSession.IsValidRoleName(role).Should().BeFalse();

    [Fact]
    public void A_role_name_longer_than_Postgres_allows_is_refused() =>
        ReadOnlySqlSession.IsValidRoleName(new string('a', 64)).Should().BeFalse();

    /// <summary>
    /// <c>0ms</c> is "disabled" to Postgres for every one of the three timeouts, so a zero or a negative
    /// value is sent as the smallest timeout there is rather than as no timeout at all. Proven here
    /// rather than live: a 1ms idle-in-transaction timeout on a real connection is a race the server
    /// wins about one run in three.
    /// </summary>
    [Theory]
    [InlineData(7_000, "7000ms")]
    [InlineData(1, "1ms")]
    [InlineData(0, "1ms")]
    [InlineData(-5, "1ms")]
    public void A_timeout_is_sent_in_milliseconds_and_never_below_one(int milliseconds, string expected) =>
        ReadOnlySqlSession.Milliseconds(TimeSpan.FromMilliseconds(milliseconds)).Should().Be(expected);

    /// <summary>
    /// The role is validated <em>and</em> bound as a parameter. Either alone would do; both is what keeps
    /// the one place a role name could reach SQL text from being the one place that matters.
    /// </summary>
    [Fact]
    public void Every_setting_is_applied_through_set_config_rather_than_a_SET_statement()
    {
        ReadOnlySqlSession.SetConfigSql.Should().Be("select set_config(@name, @value, true)");
        ReadOnlySqlSession.ReadOnlySql.Should().Be("set transaction read only");
    }

    /// <summary>
    /// The argument checks run in argument order, so a null connection is complained about before the role
    /// is ever looked at. Named for what it asserts: the role is validated too, but not here — the live
    /// suite's <c>A_role_name_that_is_not_an_identifier_never_reaches_the_database</c> is where that is
    /// proved, because it needs a connection to get past this check.
    /// </summary>
    [Fact]
    public async Task A_null_connection_is_refused_before_the_role_is_validated()
    {
        var session = new ReadOnlySqlSession(new ReadOnlySqlOptions { Role = "bad; drop table x" });

        var act = async () => await session.ExecuteAsync(null!, "select 1");

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    /// <summary>
    /// The Mode A entry point takes the same argument checks in the same order, because it is the same
    /// method: <c>ExecuteAsync</c> is a caller of <c>InTransactionAsync</c> rather than a second copy of
    /// the preamble. Two copies of <c>BEGIN; SET TRANSACTION READ ONLY; …</c> is precisely how Mode A came
    /// to have none at all.
    /// </summary>
    /// <remarks>
    /// Plain options, deliberately. This used to be written with an invalid <c>Role</c> on the session, as
    /// if the two assertions said something about role validation - they cannot, because both throw on the
    /// null argument before the role is ever looked at. The role claim is
    /// <see cref="An_invalid_role_is_refused_before_a_transaction_is_ever_begun" />, which reaches it.
    /// </remarks>
    [Fact]
    public async Task The_transaction_helper_the_query_page_uses_takes_the_same_checks()
    {
        var session = new ReadOnlySqlSession(new ReadOnlySqlOptions());

        var nullConnection = async () =>
            await session.InTransactionAsync<int>(null!, (_, _) => Task.FromResult(1));

        await nullConnection.Should().ThrowAsync<ArgumentNullException>();

        var nullWork = async () =>
            await session.InTransactionAsync<int>(new NpgsqlConnection(), null!);

        await nullWork.Should().ThrowAsync<ArgumentNullException>();
    }

    /// <summary>
    /// The role check with both arguments supplied, which is the only way to reach it: it sits after the
    /// two <see cref="ArgumentNullException" /> guards and <em>before</em> <c>BeginTransactionAsync</c>, so
    /// an invalid role never becomes a connection attempt, let alone a <c>set_config</c>.
    /// </summary>
    /// <remarks>
    /// The message is asserted rather than only the type, and it has to be: a closed
    /// <see cref="NpgsqlConnection" /> would answer <c>BeginTransactionAsync</c> with an
    /// <see cref="InvalidOperationException" /> of its own, so "it threw the right type" would pass whether
    /// the role was checked or not. Naming the role is what distinguishes the two, and it is also what
    /// proves the refusal happened before the connection was touched - this session has no connection
    /// string at all.
    /// </remarks>
    [Fact]
    public async Task An_invalid_role_is_refused_before_a_transaction_is_ever_begun()
    {
        var session = new ReadOnlySqlSession(new ReadOnlySqlOptions { Role = "bad; drop table x" });

        var act = async () =>
            await session.InTransactionAsync(new NpgsqlConnection(), (_, _) => Task.FromResult(1));

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*bad; drop table x*is not a valid Postgres role name*");
    }

    [Fact]
    public void The_defaults_are_the_conservative_ones()
    {
        var options = new ReadOnlySqlOptions();

        options.StatementTimeout.Should().Be(TimeSpan.FromSeconds(30));
        options.LockTimeout.Should().Be(TimeSpan.FromSeconds(3));
        options.IdleInTransactionTimeout.Should().Be(TimeSpan.FromSeconds(60));
        options.MaxRows.Should().Be(500);
        options.Role.Should().BeNull();
    }

    /// <summary>
    /// <c>statement_timeout = 0</c> is Postgres for <em>disabled</em>, so a host that wrote
    /// <c>TimeSpan.Zero</c> meaning "as tight as possible" would have removed the only thing stopping a
    /// console query from running until the database falls over. It is refused where it is written.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-30_000)]
    public void A_statement_timeout_that_is_not_positive_is_refused_by_the_options(int milliseconds)
    {
        var act = () => new ReadOnlySqlOptions { StatementTimeout = TimeSpan.FromMilliseconds(milliseconds) };

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void A_positive_statement_timeout_is_kept_exactly()
    {
        new ReadOnlySqlOptions { StatementTimeout = TimeSpan.FromMilliseconds(1) }
            .StatementTimeout.Should().Be(TimeSpan.FromMilliseconds(1));

        (new ReadOnlySqlOptions() with { StatementTimeout = TimeSpan.FromSeconds(5) })
            .StatementTimeout.Should().Be(TimeSpan.FromSeconds(5), "the check lives on the initialiser, " +
                "so a `with` expression is checked exactly like a constructor call");
    }
}
