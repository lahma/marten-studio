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
/// <c>ProjectionProgressStatement</c>'s, gated on the same flag <c>ShardStateSelector</c> gates its
/// ordinals on, the tenant filter is its trailing-substring comparison rather than a <c>like</c>, the
/// excluded rows are the two high-water bookkeeping names it excludes, and the high-water read is
/// <c>MartenDatabase.FetchHighestEventSequenceNumber</c>'s two branches. If one of those drifts, the
/// studio's numbers stop agreeing with Marten's own tooling, which is a worse failure than an error would
/// be because nothing says so.
/// </para>
/// <para>
/// No database: these are built commands, and what is asserted is their text and their parameters.
/// <c>ProjectionsNoDdlLiveTests</c> and <c>ExtendedProgressionLiveTests</c> are where they are run against
/// a real Postgres.
/// </para>
/// </remarks>
public class ProjectionProgressQueriesTests
{
    /// <summary>
    /// The progression table as Marten 9.35 migrates it - which is to say, with the extended columns,
    /// whatever <c>EnableExtendedProgressionTracking</c> says.
    /// </summary>
    /// <remarks>
    /// <c>EventProgressionTable</c> adds them unconditionally (#5309), so "the table has them" is the
    /// ordinary case and says nothing at all about whether anything writes them.
    /// </remarks>
    private static readonly string[] Extended =
    [
        "name", "last_seq_id", "last_updated", "heartbeat", "agent_status", "pause_reason", "running_on_node",
        "failure_category", "failure_event_sequence", "failure_event_type", "failure_event_tenant_id",
    ];

    /// <summary>
    /// A table that has only the two columns the studio reads and one it does not - the shape a store
    /// whose flag was switched on before the migration was applied still has.
    /// </summary>
    private static readonly string[] Minimal = ["name", "last_seq_id", "last_updated"];

    /// <summary>The two rows Marten's own statement excludes, which this one excludes too.</summary>
    private static readonly string[] BookkeepingRows = ["HighWaterAllocationFence", "HighWaterStuckGap"];

    /// <summary>A table without the columns is two columns, whatever the store's flag says.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_progression_read_names_two_columns_on_a_plain_store_and_never_selects_star(bool extended)
    {
        using NpgsqlCommand command = ProjectionProgressQueries.BuildProgressionRows("studio_events", Minimal, extended);

        command.CommandText.Should().StartWith("select \"name\", \"last_seq_id\"\n");
        command.CommandText.Should().Contain("from \"studio_events\".\"mt_event_progression\"");
        command.CommandText.Should().NotContain("select *");
        command.CommandText.Should().NotContain("last_updated", "nothing renders it, so nothing reads it");
        command.CommandText.Should().NotContain("agent_status");
    }

    /// <summary>
    /// The extended columns are selected when the store's own flag says something writes them - and only
    /// then.
    /// </summary>
    /// <remarks>
    /// Marten's <c>ShardStateSelector</c> walks those ordinals only under
    /// <c>Events.EnableExtendedProgressionTracking</c>, which defaults to <see langword="false" />, while
    /// <c>EventProgressionTable</c> creates the columns on every migrated table regardless of it
    /// (#5309). Reading them because the table has them is therefore reading telemetry that nothing may
    /// be maintaining, and reconstructing an <c>AgentStatus</c>, a <c>PauseReason</c> and a
    /// <c>ShardFailure</c> that <c>AllProjectionProgress</c> would not report.
    /// </remarks>
    [Fact]
    public void The_progression_read_selects_the_extended_columns_only_when_the_store_writes_them()
    {
        using NpgsqlCommand on = ProjectionProgressQueries.BuildProgressionRows("studio_events", Extended, true);

        on.CommandText.Should().StartWith(
            "select \"name\", \"last_seq_id\", \"heartbeat\", \"agent_status\", \"pause_reason\", "
            + "\"running_on_node\", \"failure_category\", \"failure_event_sequence\", \"failure_event_type\", "
            + "\"failure_event_tenant_id\"\n");

        ProjectionProgressQueries.SelectedOptionalColumns(Extended, true).Should().Equal(
            "heartbeat", "agent_status", "pause_reason", "running_on_node",
            "failure_category", "failure_event_sequence", "failure_event_type", "failure_event_tenant_id");

        // The same table, with the flag off: the columns are there and are not asked for.
        using NpgsqlCommand off = ProjectionProgressQueries.BuildProgressionRows("studio_events", Extended, false);

        off.CommandText.Should().StartWith("select \"name\", \"last_seq_id\"\n");
        off.CommandText.Should().NotContain("heartbeat").And.NotContain("pause_reason")
            .And.NotContain("failure_category");

        ProjectionProgressQueries.SelectedOptionalColumns(Extended, false).Should().BeEmpty();
    }

    /// <summary>
    /// And the catalog is still consulted, because the reverse shape exists too.
    /// </summary>
    /// <remarks>
    /// A store whose <c>EnableExtendedProgressionTracking</c> was switched on without the migration having
    /// been applied has the flag and not the columns; Marten's own read raises <c>42703</c> on it, and a
    /// read path must not be the thing that fixes that by migrating (hard rule 14). So the flag decides
    /// whether to ask and the catalog decides whether the table can answer, and both have to hold.
    /// </remarks>
    [Fact]
    public void A_flag_without_the_columns_still_selects_only_what_the_table_has()
    {
        ProjectionProgressQueries.SelectedOptionalColumns(Minimal, true).Should().BeEmpty();

        string[] halfMigrated = [.. Minimal, "heartbeat", "agent_status"];

        ProjectionProgressQueries.SelectedOptionalColumns(halfMigrated, true).Should()
            .Equal("heartbeat", "agent_status");

        using NpgsqlCommand command = ProjectionProgressQueries
            .BuildProgressionRows("studio_events", halfMigrated, true);

        command.CommandText.Should().StartWith(
            "select \"name\", \"last_seq_id\", \"heartbeat\", \"agent_status\"\n");
        command.CommandText.Should().NotContain("pause_reason");
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

        using NpgsqlCommand command = ProjectionProgressQueries.BuildProgressionRows("studio_events", columns, true);

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
        using NpgsqlCommand command = ProjectionProgressQueries.BuildProgressionRows("studio_events", Minimal, false);

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
        using NpgsqlCommand all = ProjectionProgressQueries.BuildProgressionRows("studio_events", Minimal, false);
        using NpgsqlCommand acme = ProjectionProgressQueries
            .BuildProgressionRows("studio_events", Minimal, false, "acme_corp");

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
            .BuildProgressionRows("studio_events", Minimal, false, null, TimeSpan.FromSeconds(12));

        command.CommandText.Should().EndWith("order by \"name\"");
        command.CommandTimeout.Should().Be(12);
    }

    /// <summary>A sub-second budget is a second, never Npgsql's zero, which means "wait forever".</summary>
    [Fact]
    public void A_timeout_is_never_rounded_down_to_no_timeout_at_all()
    {
        using NpgsqlCommand progression = ProjectionProgressQueries
            .BuildProgressionRows("studio_events", Minimal, false, null, TimeSpan.FromMilliseconds(40));
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
    /// and for the same reason. <c>TenantPartitionedEventsLiveTests</c> runs both against a real store.
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
        Action progression = () => ProjectionProgressQueries.BuildProgressionRows(schema, Minimal, false).Dispose();
        Action highWater = () => ProjectionProgressQueries.BuildHighWaterMark(schema, false).Dispose();

        progression.Should().Throw<ArgumentException>();
        highWater.Should().Throw<ArgumentException>();
    }

    /// <summary>A schema that needs quoting gets it, rather than being rejected.</summary>
    [Fact]
    public void A_schema_name_that_needs_quoting_is_quoted()
    {
        using NpgsqlCommand command = ProjectionProgressQueries.BuildProgressionRows("Odd Schema", Minimal, false);

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
