using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Database;
using PostgresStudio.Services.Health;

namespace MartenStudio.Tests.Database;

/// <summary>
/// Database › Sessions draws a "Show SQL" per statement its snapshot ran, on both tabs; 0.6's UI sweep found five that all
/// said "Show SQL". The reader names each by what it reads, keyed on the text its builder writes - and
/// <c>SessionsLiveTests</c> holds every statement a real read runs to a name of its own.
/// </summary>
public class SessionStatementLabelTests
{
    [Fact]
    public void Every_statement_the_reader_runs_is_named_by_what_it_reads_and_no_two_alike()
    {
        IReadOnlyList<HealthStatement> labelled = SessionActivityReader.Labelled(
        [
            new HealthStatement(HealthConnectionQueries.SessionSnapshot().Sql, []),
            new HealthStatement(HealthConnectionQueries.LockSnapshot().Sql, []),
            new HealthStatement(HealthConnectionQueries.PreparedTransactions().Sql, []),
            new HealthStatement(ConnectionsByApplicationQueries.Read().Sql, []),
            new HealthStatement(HealthConnectionQueries.LockTable().Sql, ["@limit"]),
            new HealthStatement(HealthConnectionQueries.LockStatistics().Sql, []),
        ]);

        labelled.Select(static x => x.Label).Should().Equal(
            "the sessions", "the locks", "prepared transactions", "connections by application", "who blocks whom", "lock waits by type");
        labelled[4].ParameterNames.Should().Equal(["@limit"], "naming a statement changes nothing else about it");
    }

    [Fact]
    public void A_statement_it_does_not_know_keeps_no_label_and_one_already_named_keeps_its_own()
    {
        IReadOnlyList<HealthStatement> labelled = SessionActivityReader.Labelled(
        [
            new HealthStatement("select 1", []),
            new HealthStatement(HealthConnectionQueries.SessionSnapshot().Sql, []) { Label = "somebody's own" },
        ]);

        labelled[0].Label.Should().BeNull("its Show SQL says 'Show SQL', as before");
        labelled[1].Label.Should().Be("somebody's own");
    }
}
