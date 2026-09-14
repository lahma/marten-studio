using MartenStudio.Internal.Sql;

using Npgsql;

namespace MartenStudio.Tests.Sql;

/// <summary>
/// The two event-store progress reads the studio does itself, rather than through the Marten calls that
/// migrate before they read (AGENTS.md hard rule 14).
/// </summary>
/// <remarks>
/// <para>
/// Every assertion here is about staying the same statement Marten runs. The column list is
/// <c>ProjectionProgressStatement</c>'s, the tenant filter is its trailing-substring comparison rather
/// than a <c>like</c>, the excluded rows are the two high-water bookkeeping names it excludes, and the
/// high-water read is <c>MartenDatabase.FetchHighestEventSequenceNumber</c>'s two branches. If one of
/// those drifts, the studio's numbers stop agreeing with Marten's own tooling, which is a worse failure
/// than an error would be because nothing says so.
/// </para>
/// <para>
/// No database: these are built commands, and what is asserted is their text and their parameters.
/// <c>ProjectionsNoDdlLiveTests</c> is where they are run against a real Postgres.
/// </para>
/// </remarks>
public class ProjectionProgressQueriesTests
{
    /// <summary>A progression table on a store with extended tracking switched on and migrated.</summary>
    private static readonly string[] Extended =
    [
        "name", "last_seq_id", "last_updated", "heartbeat", "agent_status", "pause_reason", "running_on_node",
        "failure_category", "failure_event_sequence", "failure_event_type", "failure_event_tenant_id",
    ];

    /// <summary>The table every store has: two columns the studio reads, and one it does not.</summary>
    private static readonly string[] Minimal = ["name", "last_seq_id", "last_updated"];

    /// <summary>The two rows Marten's own statement excludes, which this one excludes too.</summary>
    private static readonly string[] BookkeepingRows = ["HighWaterAllocationFence", "HighWaterStuckGap"];

    [Fact]
    public void The_progression_read_names_two_columns_on_a_plain_store_and_never_selects_star()
    {
        using NpgsqlCommand command = ProjectionProgressQueries.BuildProgressionRows("studio_events", Minimal);

        command.CommandText.Should().StartWith("select \"name\", \"last_seq_id\"\n");
        command.CommandText.Should().Contain("from \"studio_events\".\"mt_event_progression\"");
        command.CommandText.Should().NotContain("select *");
        command.CommandText.Should().NotContain("last_updated", "nothing renders it, so nothing reads it");
        command.CommandText.Should().NotContain("agent_status");
    }

    /// <summary>
    /// The extended columns are selected because the <em>table</em> has them, never because a flag says
    /// so.
    /// </summary>
    /// <remarks>
    /// Marten branches its own select list on <c>EnableExtendedProgressionTracking</c> and
    /// <c>UseOptimizedProjectionRebuilds</c>, which is right for Marten and wrong for a read path that
    /// must not migrate: a store whose flag was turned on without the migration having been applied has
    /// the flag and not the columns, and that read raises <c>42703</c>. What the table has is what is
    /// selected, in Marten's own order so the ordinals mean the same thing.
    /// </remarks>
    [Fact]
    public void The_progression_read_selects_the_extended_columns_the_table_actually_has()
    {
        using NpgsqlCommand command = ProjectionProgressQueries.BuildProgressionRows("studio_events", Extended);

        command.CommandText.Should().StartWith(
            "select \"name\", \"last_seq_id\", \"heartbeat\", \"agent_status\", \"pause_reason\", "
            + "\"running_on_node\", \"failure_category\", \"failure_event_sequence\", \"failure_event_type\", "
            + "\"failure_event_tenant_id\"\n");

        ProjectionProgressQueries.SelectedOptionalColumns(Extended).Should().Equal(
            "heartbeat", "agent_status", "pause_reason", "running_on_node",
            "failure_category", "failure_event_sequence", "failure_event_type", "failure_event_tenant_id");

        ProjectionProgressQueries.SelectedOptionalColumns(Minimal).Should().BeEmpty();
    }

    /// <summary>
    /// The three <c>UseOptimizedProjectionRebuilds</c> columns are deliberately not read.
    /// </summary>
    /// <remarks>
    /// Nothing the studio draws is derived from <c>mode</c>, <c>rebuild_threshold</c> or
    /// <c>assigned_node</c>, and a select list that names a column nobody renders is row width paid for
    /// on every poll of every circuit. This is the one place the studio's statement is narrower than
    /// Marten's, so it is pinned rather than left to be rediscovered as a bug.
    /// </remarks>
    [Fact]
    public void The_optimized_rebuild_columns_are_not_read()
    {
        string[] columns = [.. Extended, "mode", "rebuild_threshold", "assigned_node"];

        using NpgsqlCommand command = ProjectionProgressQueries.BuildProgressionRows("studio_events", columns);

        command.CommandText.Should().NotContain("mode");
        command.CommandText.Should().NotContain("rebuild_threshold");
        command.CommandText.Should().NotContain("assigned_node");
    }

    /// <summary>
    /// The two high-water bookkeeping rows are excluded in SQL, by value and not by interpolation.
    /// </summary>
    [Fact]
    public void The_high_water_bookkeeping_rows_are_excluded_as_a_parameter()
    {
        using NpgsqlCommand command = ProjectionProgressQueries.BuildProgressionRows("studio_events", Minimal);

        command.CommandText.Should().Contain("and \"name\" <> all(@bookkeeping)");
        command.CommandText.Should().NotContain("HighWaterAllocationFence", "the names are values, not SQL text");

        command.Parameters["bookkeeping"].Value.Should().BeEquivalentTo(BookkeepingRows);
    }

    /// <summary>
    /// The tenant filter compares a trailing substring, and is a parameter that may be null.
    /// </summary>
    /// <remarks>
    /// Marten changed this from <c>name like '%:' || tenant</c> for a reason the studio inherits:
    /// <c>_</c> is both a legal tenant-id character and a <c>like</c> single-character wildcard, so
    /// tenant <c>acme_corp</c> also matched <c>acmeXcorp</c>'s rows. One statement serves both cases, so
    /// a filtered read and an unfiltered one share a plan.
    /// </remarks>
    [Fact]
    public void The_tenant_filter_is_a_trailing_substring_and_never_a_like_pattern()
    {
        using NpgsqlCommand all = ProjectionProgressQueries.BuildProgressionRows("studio_events", Minimal);
        using NpgsqlCommand acme = ProjectionProgressQueries.BuildProgressionRows("studio_events", Minimal, "acme_corp");

        all.CommandText.Should().Be(acme.CommandText, "one statement means one plan, filtered or not");
        all.CommandText.Should().Contain(
            "where (@tenantSuffix is null or right(\"name\", char_length(@tenantSuffix)) = @tenantSuffix)");
        all.CommandText.Should().NotContain("like");

        all.Parameters["tenantSuffix"].Value.Should().Be(DBNull.Value);
        acme.Parameters["tenantSuffix"].Value.Should().Be(":acme_corp");
    }

    [Fact]
    public void The_progression_read_is_ordered_and_carries_the_callers_timeout()
    {
        using NpgsqlCommand command = ProjectionProgressQueries
            .BuildProgressionRows("studio_events", Minimal, null, TimeSpan.FromSeconds(12));

        command.CommandText.Should().EndWith("order by \"name\"");
        command.CommandTimeout.Should().Be(12);
    }

    /// <summary>A sub-second budget is a second, never Npgsql's zero, which means "wait forever".</summary>
    [Fact]
    public void A_timeout_is_never_rounded_down_to_no_timeout_at_all()
    {
        using NpgsqlCommand progression = ProjectionProgressQueries
            .BuildProgressionRows("studio_events", Minimal, null, TimeSpan.FromMilliseconds(40));
        using NpgsqlCommand highWater = ProjectionProgressQueries
            .BuildHighWaterMark("studio_events", false, TimeSpan.FromMilliseconds(40));

        progression.CommandTimeout.Should().Be(1);
        highWater.CommandTimeout.Should().Be(1);
    }

    /// <summary>
    /// The high-water read is Marten's own, including the branch that exists because of per-tenant
    /// partitioning.
    /// </summary>
    /// <remarks>
    /// Under <c>UseTenantPartitionedEvents</c> every tenant's events draw <c>seq_id</c> from a partition
    /// sequence of their own, so the store-global <c>mt_events_sequence</c> is never advanced and its
    /// <c>last_value</c> reads as 1. Marten reads <c>max(seq_id)</c> in that mode; so does the studio,
    /// and for the same reason.
    /// </remarks>
    [Fact]
    public void The_high_water_read_is_the_sequence_and_the_events_table_under_tenant_partitioning()
    {
        using NpgsqlCommand ordinary = ProjectionProgressQueries.BuildHighWaterMark("studio_events", false);
        using NpgsqlCommand partitioned = ProjectionProgressQueries.BuildHighWaterMark("studio_events", true);

        ordinary.CommandText.Should().Be("select last_value from \"studio_events\".\"mt_events_sequence\"");
        partitioned.CommandText.Should()
            .Be("select coalesce(max(\"seq_id\"), 0) from \"studio_events\".\"mt_events\"");

        ordinary.Parameters.Should().BeEmpty("there is nothing here a caller could supply");
    }

    /// <summary>
    /// Every identifier is quoted through the builder, and a schema name that could not be one is refused
    /// rather than escaped (hard rule 4).
    /// </summary>
    [Theory]
    [InlineData("has\"quote")]
    [InlineData("")]
    [InlineData("   ")]
    public void A_schema_name_that_is_not_an_identifier_is_refused(string schema)
    {
        Action progression = () => ProjectionProgressQueries.BuildProgressionRows(schema, Minimal).Dispose();
        Action highWater = () => ProjectionProgressQueries.BuildHighWaterMark(schema, false).Dispose();

        progression.Should().Throw<ArgumentException>();
        highWater.Should().Throw<ArgumentException>();
    }

    /// <summary>A schema that needs quoting gets it, rather than being rejected.</summary>
    [Fact]
    public void A_schema_name_that_needs_quoting_is_quoted()
    {
        using NpgsqlCommand command = ProjectionProgressQueries.BuildProgressionRows("Odd Schema", Minimal);

        command.CommandText.Should().Contain("from \"Odd Schema\".\"mt_event_progression\"");
    }

    /// <summary>The names the studio reads are Marten's, spelled once.</summary>
    [Fact]
    public void The_object_names_are_Martens_own()
    {
        ProjectionProgressQueries.ProgressionTable.Should().Be("mt_event_progression");
        ProjectionProgressQueries.EventsSequence.Should().Be("mt_events_sequence");
    }
}
