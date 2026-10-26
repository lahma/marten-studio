using MartenStudio.Internal.Sql;
using MartenStudio.Services.Events;

using Npgsql;

using NpgsqlTypes;

namespace MartenStudio.Tests.Sql;

/// <summary>
/// M3's statements: the grouped view's read, a group's events for the bulk skip, and the list's group filter - the
/// studio's own SQL over <c>mt_doc_deadletterevent</c>, every key and value a parameter (hard rule 4), the body's own
/// <c>TenantId</c> as the tenant axis and each letter's event looked up in the scope (hard rule 15).
/// </summary>
public class DeadLetterGroupQueriesTests
{
    private static readonly DeadLetterJsonKeys Keys = DeadLetterJsonKeys.For(null);

    private static readonly DeadLetterGroupKey Group = new(
        "Orders'; drop table x; --", "All", "order_placed", "InvalidOperationException", "Failure to apply event #{n} Id({guid})");

    private static DocumentTableInfo Table => new()
    {
        Schema = "studio_events",
        Table = "mt_doc_deadletterevent",
        Alias = "deadletterevent",
    };

    private static readonly string[] EventColumns = ["seq_id", "id", "stream_id", "version", "type", "data", "timestamp"];

    private static EventTableInfo Events(bool tenantId = true, bool skipped = true) => new()
    {
        Schema = "studio_events",
        EventColumns = [.. EventColumns
            .Concat(tenantId ? ["tenant_id"] : [])
            .Concat(skipped ? ["is_skipped"] : [])],
        StreamColumns = ["id", "type", "version", "timestamp"],
    };

    private static DeadLetterEventSource Source(bool perTenant = false, bool tenantId = true, bool skipped = true) =>
        new(Events(tenantId, skipped), perTenant);

    /// <summary>
    /// The view's read: the parts of each letter's key and its event's type, as columns - never the body, never a regex
    /// in the database - newest first as the list orders, and bounded.
    /// </summary>
    [Fact]
    public void The_grouped_read_selects_columns_newest_first_and_is_bounded()
    {
        using NpgsqlCommand command = DeadLetterGroupQueries.BuildGroupRows(Table, Keys, Source(), "acme", 1_000_000);

        string sql = command.CommandText;

        sql.Should().StartWith(
            "select d.\"data\" ->> @projection_key as projection,\n"
            + "       d.\"data\" ->> @shard_key as shard,\n"
            + "       d.\"data\" ->> @exception_key as exception_type,\n"
            + "       d.\"data\" ->> @tenant_key as tenant,\n"
            + "       (d.\"data\" ->> @timestamp_key)::timestamptz as at,\n"
            + "       pg_catalog.left(d.\"data\" ->> @message_key, @fp_length) as message,\n"
            + "       ev.event_type, ev.is_skipped, ev.event_key\n"
            + "from \"studio_events\".\"mt_doc_deadletterevent\" as d\n"
            + "left join lateral (");
        sql.Should().EndWith("order by (d.\"data\" ->> @timestamp_key)::timestamptz desc, d.\"id\" desc\nlimit @limit");
        sql.Should().NotContain("select *").And.NotContain("d.\"data\",").And.NotContain("regexp_replace",
            "no body crosses the wire, and the database fingerprints nothing on the view's read");

        command.Parameters["limit"].Value.Should().Be(DeadLetterGroupLimits.MaxLettersGrouped + 1, "the bound and one more, whatever is asked");
        command.Parameters["fp_length"].Value.Should().Be(DeadLetterFingerprint.MaxInputLength);
        command.Parameters["message_key"].Value.Should().Be("ExceptionMessage");
        command.Parameters["timestamp_key"].Value.Should().Be("Timestamp");
    }

    /// <summary>The list's tenant axis, kept: the body's own <c>TenantId</c>, and with no tenant every tenant's letters.</summary>
    [Fact]
    public void The_tenant_axis_is_the_bodys_own_property()
    {
        using NpgsqlCommand scoped = DeadLetterGroupQueries.BuildGroupRows(Table, Keys, Source(), "acme", 200);
        using NpgsqlCommand everyone = DeadLetterGroupQueries.BuildGroupRows(Table, Keys, Source(), null, 200);

        scoped.CommandText.Should().Contain("where (@tenant is null or d.\"data\" ->> @tenant_key = @tenant)");
        scoped.Parameters["tenant"].Value.Should().Be("acme");
        scoped.Parameters["tenant_key"].Value.Should().Be("TenantId");
        everyone.Parameters["tenant"].Value.Should().Be(DBNull.Value);
        everyone.CommandText.Should().Be(scoped.CommandText, "one statement serves both");
    }

    /// <summary>
    /// A letter's event is looked up by an index probe on <c>seq_id</c>, never a scan, and in the scope: the expansion's
    /// tenant clause, so another tenant's event lends a letter no type.
    /// </summary>
    [Fact]
    public void Each_letters_event_is_looked_up_in_the_scope()
    {
        using NpgsqlCommand command = DeadLetterGroupQueries.BuildGroupRows(Table, Keys, Source(), "acme", 200);

        string sql = command.CommandText;

        sql.Should().Contain("left join lateral (select e.\"type\" as event_type, e.\"is_skipped\" as is_skipped, e.\"seq_id\" as seq_id, e.\"seq_id\"::text as event_key\n");
        sql.Should().Contain("from \"studio_events\".\"mt_events\" as e\n");
        sql.Should().Contain("where e.\"seq_id\" = case when d.\"data\" ->> @sequence_key ~ '^[0-9]{1,18}$' then (d.\"data\" ->> @sequence_key)::bigint end\n");
        sql.Should().Contain("and (@tenant is null or e.\"tenant_id\" = @tenant)\n");
        sql.Should().Contain("limit 1) as ev on true\n");
        sql.Should().NotContain("e.\"tenant_id\" = d.\"data\" ->> @tenant_key", "outside per-tenant sequences a seq_id is the store's");
        command.Parameters["sequence_key"].Value.Should().Be("EventSequence");
    }

    /// <summary>Under per-tenant sequences a letter's number is its own tenant's, so the lookup is by both.</summary>
    [Fact]
    public void Under_per_tenant_sequences_the_event_is_the_letters_own_tenants()
    {
        using NpgsqlCommand command = DeadLetterGroupQueries.BuildGroupRows(Table, Keys, Source(perTenant: true), null, 200);

        command.CommandText.Should().Contain("and e.\"tenant_id\" = d.\"data\" ->> @tenant_key\n");
        command.CommandText.Should().Contain("e.\"seq_id\"::text || '/' || e.\"tenant_id\" as event_key");
    }

    [Fact]
    public void A_store_with_no_is_skipped_column_reads_no_skips()
    {
        using NpgsqlCommand command = DeadLetterGroupQueries.BuildGroupRows(Table, Keys, Source(skipped: false), null, 200);

        command.CommandText.Should().Contain("null::boolean as is_skipped").And.NotContain("e.\"is_skipped\"");
    }

    /// <summary>No events table, nothing to look up: the letters are grouped with no event type, and nothing is created.</summary>
    [Fact]
    public void A_database_with_no_events_table_groups_with_no_event_type()
    {
        using NpgsqlCommand command = DeadLetterGroupQueries.BuildGroupRows(Table, Keys, events: null, null, 200);

        command.CommandText.Should().Contain("null::text as event_type, null::boolean as is_skipped, null::text as event_key")
            .And.NotContain("mt_events").And.NotContain("lateral");
    }

    /// <summary>
    /// The fingerprint is <see cref="DeadLetterFingerprint.Rules" /> as nested <c>regexp_replace</c> calls, every pattern
    /// and replacement a parameter - the rule's text never reaches the statement.
    /// </summary>
    [Fact]
    public void The_fingerprint_is_the_rule_bound_as_parameters()
    {
        using var command = new NpgsqlCommand();

        string expression = DeadLetterGroupQueries.FingerprintOf(command, "x.message");

        int rules = DeadLetterFingerprint.Rules.Count;

        expression.Should().StartWith("pg_catalog.btrim(" + string.Concat(Enumerable.Repeat("pg_catalog.regexp_replace(", rules))
            + "pg_catalog.left(x.message, @fp_length), @fp_pattern_0, @fp_replacement_0, 'g')");
        expression.Should().EndWith(", @fp_pattern_" + (rules - 1) + ", @fp_replacement_" + (rules - 1) + ", 'g'), ' ')");
        command.Parameters["fp_length"].Value.Should().Be(DeadLetterFingerprint.MaxInputLength);

        for (int index = 0; index < rules; index++)
        {
            DeadLetterFingerprintRule rule = DeadLetterFingerprint.Rules[index];

            command.Parameters["fp_pattern_" + index].Value.Should().Be(rule.Pattern);
            command.Parameters["fp_pattern_" + index].NpgsqlDbType.Should().Be(NpgsqlDbType.Text);
            command.Parameters["fp_replacement_" + index].Value.Should().Be(rule.PostgresReplacement);
            if (rule.Placeholder.Trim().Length > 0)
            {
                expression.Should().NotContain(rule.Placeholder, "a placeholder is a parameter's value, not text");
            }

            expression.Should().NotContain(rule.Pattern, "a pattern is a parameter's value, not text");
        }

        // Asked twice in one statement, the rule is bound once.
        int bound = command.Parameters.Count;
        DeadLetterGroupQueries.FingerprintOf(command, "y.message");
        command.Parameters.Count.Should().Be(bound);
    }

    /// <summary>A group's events for the skip: inner lookup, the group's predicates, not skipped yet, lowest first, capped.</summary>
    [Fact]
    public void A_groups_events_to_skip_are_its_unskipped_events_lowest_first_and_capped()
    {
        using NpgsqlCommand command = DeadLetterGroupQueries.BuildGroupSequencesToSkip(Table, Keys, Source(), "acme", Group, 5_000);

        string sql = command.CommandText;

        sql.Should().StartWith("select distinct ev.seq_id\nfrom \"studio_events\".\"mt_doc_deadletterevent\" as d\njoin lateral (");
        sql.Should().Contain("where (@tenant is null or d.\"data\" ->> @tenant_key = @tenant)\n");
        sql.Should().Contain("  and d.\"data\" ->> @projection_key is not distinct from @group_projection\n")
            .And.Contain("  and d.\"data\" ->> @shard_key is not distinct from @group_shard\n")
            .And.Contain("  and d.\"data\" ->> @exception_key is not distinct from @group_exception\n")
            .And.Contain(") is not distinct from @group_fingerprint\n")
            .And.Contain("  and ev.event_type is not distinct from @group_event_type\n");
        sql.Should().EndWith("  and not ev.is_skipped\norder by ev.seq_id\nlimit @limit");

        command.Parameters["limit"].Value.Should().Be(DeadLetterGroupLimits.MaxEventsPerSkip + 1, "the cap and one more, whatever is asked");
        command.Parameters["group_projection"].Value.Should().Be(Group.ProjectionName);
        command.Parameters["group_fingerprint"].Value.Should().Be(Group.Fingerprint);
        command.Parameters["group_event_type"].Value.Should().Be("order_placed");
        sql.Should().NotContain("drop table").And.NotContain("InvalidOperationException").And.NotContain("order_placed");
    }

    /// <summary>A missing part of the key is a value: <c>is not distinct from</c> a null parameter.</summary>
    [Fact]
    public void A_missing_part_of_the_key_is_matched_as_missing()
    {
        var group = new DeadLetterGroupKey(null, null, null, null, null);

        using NpgsqlCommand command = DeadLetterGroupQueries.BuildGroupEventCounts(Table, Keys, Source(), null, group);

        foreach (string name in new[] { "group_projection", "group_shard", "group_exception", "group_fingerprint", "group_event_type" })
        {
            command.Parameters[name].Value.Should().Be(DBNull.Value);
            command.Parameters[name].NpgsqlDbType.Should().Be(NpgsqlDbType.Varchar, "a typed null, which Postgres can compare");
        }

        command.CommandText.Should().StartWith(
            "select count(distinct ev.event_key) filter (where not ev.is_skipped) as not_skipped,\n"
            + "       count(distinct ev.event_key) filter (where ev.is_skipped) as skipped\n");
    }

    /// <summary>Under per-tenant sequences the events to skip are read in one tenant, or not at all.</summary>
    [Fact]
    public void Under_per_tenant_sequences_the_events_to_skip_need_a_tenant()
    {
        Action building = () => DeadLetterGroupQueries.BuildGroupSequencesToSkip(Table, Keys, Source(perTenant: true), null, Group, 10);

        building.Should().Throw<ArgumentException>().WithParameterName("tenantId");
    }

    [Fact]
    public void A_store_that_records_no_skips_has_no_events_to_skip()
    {
        Action counting = () => DeadLetterGroupQueries.BuildGroupEventCounts(Table, Keys, Source(skipped: false), null, Group);

        counting.Should().Throw<ArgumentException>();
    }

    /// <summary>A soft-deleted dead-letter table keeps Marten's default filter, as the list and the badge do.</summary>
    [Fact]
    public void A_soft_deleted_table_keeps_Martens_default_filter()
    {
        DocumentTableInfo softDeleted = Table with
        {
            SoftDeleteEnabled = true,
            MetadataColumns = [new DocumentMetadataColumnInfo(DocumentMetadataColumn.IsSoftDeleted, "mt_deleted")],
        };

        using NpgsqlCommand groups = DeadLetterGroupQueries.BuildGroupRows(softDeleted, Keys, Source(), null, 200);
        using NpgsqlCommand events = DeadLetterGroupQueries.BuildGroupSequencesToSkip(softDeleted, Keys, Source(), null, Group, 10);

        groups.CommandText.Should().Contain("  and d.\"mt_deleted\" = false\n");
        events.CommandText.Should().Contain("  and d.\"mt_deleted\" = false\n");
    }

    /// <summary>
    /// The list, opened on a group: the same predicates as the skip, over a <c>left</c> lookup so a letter whose event is gone
    /// still matches a group with no event type - and the list with no group is the statement it always was.
    /// </summary>
    [Fact]
    public void The_list_opened_on_a_group_uses_the_groups_own_predicates()
    {
        using NpgsqlCommand plain = DeadLetterQueries.BuildPage(Table, Keys, new DeadLetterPageQuery { TenantId = "acme" });
        using NpgsqlCommand grouped = DeadLetterQueries.BuildPage(
            Table, Keys, new DeadLetterPageQuery { TenantId = "acme", Group = Group, Events = Source() });

        plain.CommandText.Should().NotContain("lateral").And.NotContain("group_");

        grouped.CommandText.Should().StartWith(
            "select d.\"id\", d.\"data\"\nfrom \"studio_events\".\"mt_doc_deadletterevent\" as d\nleft join lateral (");
        grouped.CommandText.Should().Contain("  and ev.event_type is not distinct from @group_event_type\n")
            .And.Contain(") is not distinct from @group_fingerprint\n");
        grouped.Parameters["tenant"].Value.Should().Be("acme");
        grouped.Parameters.Where(static x => x.ParameterName == "tenant").Should().ContainSingle("one value per name");
        grouped.Parameters.Where(static x => x.ParameterName == "projection_key").Should().ContainSingle();
    }

    /// <summary>With no events table the group's event type cannot be read, so only a group without one matches.</summary>
    [Fact]
    public void The_list_with_no_events_table_matches_only_a_group_with_no_event_type()
    {
        using NpgsqlCommand command = DeadLetterQueries.BuildPage(Table, Keys, new DeadLetterPageQuery { Group = Group });

        command.CommandText.Should().Contain("  and @group_event_type is null\n").And.NotContain("lateral");
    }
}
