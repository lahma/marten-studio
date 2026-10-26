using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Database;

namespace MartenStudio.Tests.Sql;

/// <summary>
/// Issue #8 G9's statement (W5-API): this database's client sessions by <c>application_name</c>, over every session rather
/// than the capped list, with the studio's own reading session left out - and the reader's fold of its rows.
/// </summary>
public class ConnectionsByApplicationQueriesTests
{
    [Fact]
    public void The_statement_counts_this_databases_client_sessions_without_the_reading_one_and_binds_its_cap()
    {
        HealthQuery query = ConnectionsByApplicationQueries.Read();

        query.Sql.Should().Contain("a.datname = current_database()")
            .And.Contain(HealthConnectionQueries.ClientSession)
            .And.Contain("a.pid <> pg_catalog.pg_backend_pid()")
            .And.Contain("grouping sets ((), (s.application))", "the database's total is Postgres' count, never a sum of the capped list")
            .And.Contain("pg_catalog.pg_has_role(current_user, 'pg_read_all_stats', 'USAGE')")
            .And.Contain("count(*) filter (where s.state is null) as state_unknown", "another role's session is counted, never called idle");
        query.Sql.Should().NotContain("pg_locks").And.NotContain("query", "no statement text is read for a count");
        query.Parameters.Should().ContainSingle().Which.Value.Should().Be(ConnectionsByApplicationQueries.MaxApplications);
    }

    [Fact]
    public void The_rows_fold_into_the_database_row_the_applications_and_the_budget()
    {
        SessionActivityReader.ApplicationRow Row(bool total, string? application, long sessions, long unknown = 0) => new(
            false, 100, 3, 12, total, new SessionApplicationCounts(total ? null : application, sessions, 0, 0, 0, 0, unknown, null), 2);

        SessionConnections connections = SessionActivityReader.Connections(
            [Row(true, null, 5, unknown: 1), Row(false, "orders-api", 4, unknown: 1), Row(false, "", 1)])!;

        connections.Database.Total.Should().Be(5);
        connections.Database.Application.Should().BeNull();
        connections.Applications.Select(static x => x.Application).Should().Equal("orders-api", "");
        connections.ApplicationCount.Should().Be(2);
        connections.AllSessionsVisible.Should().BeFalse("one session's state was not readable, and the role has no pg_read_all_stats");
        connections.ToBudget().Should().Be(new SessionConnectionBudget(100, 3, 12, false));

        SessionActivityReader.Connections([]).Should().BeNull("a statement that answered nothing read nothing");
    }
}
