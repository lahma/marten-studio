using MartenStudio.Internal.Sql;

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
    /// The role is validated <em>and</em> bound as a parameter. Either alone would do; both is what keeps
    /// the one place a role name could reach SQL text from being the one place that matters.
    /// </summary>
    [Fact]
    public void Every_setting_is_applied_through_set_config_rather_than_a_SET_statement()
    {
        ReadOnlySqlSession.SetConfigSql.Should().Be("select set_config(@name, @value, true)");
        ReadOnlySqlSession.ReadOnlySql.Should().Be("set transaction read only");
    }

    [Fact]
    public async Task An_invalid_role_is_refused_before_a_connection_is_touched()
    {
        var session = new ReadOnlySqlSession(new ReadOnlySqlOptions { Role = "bad; drop table x" });

        var act = async () => await session.ExecuteAsync(null!, "select 1");

        await act.Should().ThrowAsync<ArgumentNullException>(
            "the null connection is what should be complained about second; the role check comes first " +
            "only once a connection exists");
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
}
