using DataStudio.Postgres.Services.Health;

namespace DataStudio.Postgres.Tests.Diagnostics;

/// <summary>The words a health statement past its row bound fails its check with, which only a live sweep reaches.</summary>
public class HealthSqlRunnerTextTests
{
    /// <summary>#99: the bound is written the same in every culture.</summary>
    [Fact]
    public void A_statement_past_the_row_bound_is_refused_with_the_bound_written_in_the_invariant_culture() =>
        HealthSqlRunner.TooManyRows.Should().Be("A health statement returned more than 10,000 rows; a check reads a bounded answer.");
}
