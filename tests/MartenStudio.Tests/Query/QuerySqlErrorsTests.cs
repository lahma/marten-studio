using MartenStudio.Internal.Sql;
using MartenStudio.Services.Query;

namespace MartenStudio.Tests.Query;

/// <summary>
/// The two SQLSTATEs a person reading the SQL console actually needs explained.
/// </summary>
/// <remarks>
/// <c>25006</c> is the read-only transaction refusing a write, and saying so is the difference between
/// "the studio is broken" and "the studio is doing exactly what it says it does" (D13). <c>57014</c> is
/// the statement timeout, and naming the seconds turns it into something a host can change.
/// </remarks>
public class QuerySqlErrorsTests
{
    private static SqlError Error(string sqlState) => new(sqlState, "whatever Postgres said", 0, null, null);

    [Fact]
    public void The_read_only_transaction_refusal_says_that_is_what_it_is()
    {
        QuerySqlErrors.Describe(Error("25006"), TimeSpan.FromSeconds(30))
            .Should().Contain("read-only transaction").And.Contain("changes nothing");
    }

    [Fact]
    public void The_timeout_names_the_seconds_and_the_option_that_sets_them()
    {
        QuerySqlErrors.Describe(Error("57014"), TimeSpan.FromSeconds(12))
            .Should().Contain("timed out after 12 s").And.Contain("MartenStudioOptions.QueryTimeout");
    }

    [Fact]
    public void The_lock_timeout_says_the_console_never_queues_behind_a_migration()
    {
        QuerySqlErrors.Describe(Error("55P03"), TimeSpan.FromSeconds(30)).Should().Contain("lock");
    }

    [Fact]
    public void A_narrowed_role_refusal_names_the_option_that_narrowed_it()
    {
        QuerySqlErrors.Describe(Error("42501"), TimeSpan.FromSeconds(30))
            .Should().Contain("MartenStudioOptions.SqlConsoleRole");
    }

    [Fact]
    public void An_ordinary_syntax_error_needs_no_headline_of_ours()
    {
        QuerySqlErrors.Describe(Error("42601"), TimeSpan.FromSeconds(30)).Should().BeNull();
        QuerySqlErrors.Describe(null, TimeSpan.FromSeconds(30)).Should().BeNull();
    }

    [Fact]
    public void The_summary_is_the_sqlstate_and_the_message()
    {
        QuerySqlErrors.Summarize(Error("42601")).Should().Be("42601: whatever Postgres said");
    }
}
