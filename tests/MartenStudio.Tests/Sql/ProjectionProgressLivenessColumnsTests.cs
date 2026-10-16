using MartenStudio.Internal.Sql;

using Npgsql;

namespace MartenStudio.Tests.Sql;

/// <summary>
/// F2's columns on the projections screen's own read: <c>last_updated</c> (the liveness column) and the two per-row
/// thresholds are read whatever <c>EnableExtendedProgressionTracking</c> says, when the table has them, beside the
/// server's own read time - so "last progress 12 s ago" is server time minus server time.
/// </summary>
public class ProjectionProgressLivenessColumnsTests
{
    private static readonly string[] Migrated =
    [
        "name", "last_seq_id", "last_updated", "heartbeat", "agent_status", "warning_behind_threshold", "critical_behind_threshold",
    ];

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Last_updated_and_the_thresholds_are_read_whatever_the_flag_says(bool extended)
    {
        using NpgsqlCommand command = ProjectionProgressQueries.BuildProgressionRows("studio_events", Migrated, extended);

        command.CommandText.Should().StartWith(
            "select \"name\", \"last_seq_id\", transaction_timestamp() as \"read_at\", \"last_updated\", "
            + "\"warning_behind_threshold\", \"critical_behind_threshold\"");

        ProjectionProgressQueries.SelectedColumns(Migrated, extended).Take(3).Should()
            .Equal("last_updated", "warning_behind_threshold", "critical_behind_threshold");
    }

    [Fact]
    public void A_table_without_them_reads_neither_and_still_carries_the_read_time()
    {
        using NpgsqlCommand command = ProjectionProgressQueries.BuildProgressionRows("studio_events", ["name", "last_seq_id"], false);

        command.CommandText.Should().StartWith("select \"name\", \"last_seq_id\", transaction_timestamp() as \"read_at\"\n");
        ProjectionProgressQueries.SelectedColumns(["name", "last_seq_id"], false).Should().BeEmpty();
    }
}
