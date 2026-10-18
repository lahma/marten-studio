using System.Text.Json;

using JasperFx.Events.Daemon;
using JasperFx.MultiTenancy;

using Marten;

using MartenStudio.Internal.Sql;

using Npgsql;

namespace MartenStudio.Tests.Sql;

/// <summary>
/// DL: the dead-letter list's own SQL, which replaced a Marten LINQ query (<c>ToListAsync</c> ensures storage first,
/// AGENTS.md hard rule 14). Each test pins one thing the LINQ query did and this statement still does.
/// </summary>
public class DeadLetterQueriesTests
{
    private static readonly DeadLetterJsonKeys Keys = DeadLetterJsonKeys.For(null);

    private static DocumentTableInfo Table => new()
    {
        Schema = "studio_events",
        Table = "mt_doc_deadletterevent",
        Alias = "deadletterevent",
    };

    [Fact]
    public void The_page_selects_the_row_key_and_the_body_and_nothing_else()
    {
        using NpgsqlCommand command = DeadLetterQueries.BuildPage(Table, Keys, new DeadLetterPageQuery());

        command.CommandText.Should().StartWith(
            "select d.\"id\", d.\"data\"\nfrom \"studio_events\".\"mt_doc_deadletterevent\" as d\n");
        command.CommandText.Should().NotContain("select *");
        command.CommandText.Should().NotContain("tenant_id", "Marten creates no tenant column for DeadLetterEvent on any store");
    }

    /// <summary>
    /// <c>DeadLetterEvent</c> is <c>SingleTenanted()</c> on every store, so the session's tenant filtered nothing and the
    /// LINQ query narrowed on the body's own <c>TenantId</c>. So does this - and a scope with no tenant reads them all.
    /// </summary>
    [Fact]
    public void The_tenant_axis_is_the_bodys_own_property()
    {
        using NpgsqlCommand scoped = DeadLetterQueries.BuildPage(Table, Keys, new DeadLetterPageQuery { TenantId = "acme" });
        using NpgsqlCommand everyone = DeadLetterQueries.BuildPage(Table, Keys, new DeadLetterPageQuery());

        scoped.CommandText.Should().Contain("(@tenant is null or d.\"data\" ->> @tenant_key = @tenant)");
        scoped.Parameters["tenant"].Value.Should().Be("acme");
        scoped.Parameters["tenant_key"].Value.Should().Be("TenantId");

        everyone.Parameters["tenant"].Value.Should().Be(DBNull.Value);
        everyone.CommandText.Should().Be(scoped.CommandText, "one statement serves every filter combination");
    }

    [Fact]
    public void Every_filter_is_a_parameter_and_no_value_reaches_the_text()
    {
        var query = new DeadLetterPageQuery
        {
            TenantId = "tenant-x",
            ProjectionName = "Orders'; drop table x; --",
            ShardName = "All",
            ExceptionType = "Npgsql",
            Offset = 40,
            Limit = 21,
        };

        using NpgsqlCommand command = DeadLetterQueries.BuildPage(Table, Keys, query);

        command.CommandText.Should().Contain("(@projection is null or d.\"data\" ->> @projection_key = @projection)");
        command.CommandText.Should().Contain("(@shard is null or d.\"data\" ->> @shard_key = @shard)");
        command.Parameters["projection"].Value.Should().Be(query.ProjectionName);
        command.Parameters["shard"].Value.Should().Be("All");
        command.Parameters["offset"].Value.Should().Be(40);
        command.Parameters["limit"].Value.Should().Be(21);

        foreach (string value in new[] { "tenant-x", "drop table", "Npgsql", "TenantId", "ProjectionName" })
        {
            command.CommandText.Should().NotContain(value, "values and JSON keys are parameters, never text");
        }
    }

    /// <summary>
    /// Marten turned <c>ExceptionType.Contains(x)</c> into an escaped, case-sensitive <c>LIKE '%x%'</c>. A
    /// <c>strpos</c> matches the same rows and has no wildcard for a <c>%</c> or an <c>_</c> in the filter to become.
    /// </summary>
    [Fact]
    public void The_exception_filter_is_a_case_sensitive_substring_with_no_wildcards()
    {
        using NpgsqlCommand command = DeadLetterQueries.BuildPage(Table, Keys, new DeadLetterPageQuery { ExceptionType = "50%_x" });

        command.CommandText.Should().Contain("pg_catalog.strpos(d.\"data\" ->> @exception_key, @exception) > 0");
        command.CommandText.Should().NotContainEquivalentOf("like");
        command.Parameters["exception"].Value.Should().Be("50%_x");
    }

    /// <summary>
    /// <c>OrderByDescending(x =&gt; x.Timestamp)</c>, through a plain cast - the body of Marten's own
    /// <c>mt_immutable_timestamptz</c>, which a database Marten never ensured may not have - and the row key after it,
    /// so two failures in the same instant cannot swap across a page boundary.
    /// </summary>
    [Fact]
    public void Newest_failure_first_and_the_id_breaks_ties()
    {
        using NpgsqlCommand command = DeadLetterQueries.BuildPage(Table, Keys, new DeadLetterPageQuery());

        command.CommandText.Should().Contain("order by (d.\"data\" ->> @timestamp_key)::timestamptz desc, d.\"id\" desc\n");
        command.CommandText.Should().NotContain("mt_immutable_timestamptz");
        command.Parameters["timestamp_key"].Value.Should().Be("Timestamp");
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(51, 51)]
    [InlineData(1_000_000, DeadLetterQueries.MaxLimit)]
    public void The_page_size_is_bounded(int asked, int emitted)
    {
        using NpgsqlCommand command = DeadLetterQueries.BuildPage(Table, Keys, new DeadLetterPageQuery { Limit = asked, Offset = -5 });

        command.Parameters["limit"].Value.Should().Be(emitted);
        command.Parameters["offset"].Value.Should().Be(0, "a negative offset is the first page");
    }

    /// <summary>The keys are spelled the way the store's serializer writes them, which is what Marten's LINQ read.</summary>
    [Fact]
    public void The_json_keys_follow_the_serializers_casing()
    {
        DeadLetterJsonKeys.For(null).Should().Be(new DeadLetterJsonKeys("TenantId", "ProjectionName", "ShardName", "ExceptionType", "Timestamp"));
        DeadLetterJsonKeys.For(JsonNamingPolicy.CamelCase).Should().Be(
            new DeadLetterJsonKeys("tenantId", "projectionName", "shardName", "exceptionType", "timestamp"));
        DeadLetterJsonKeys.For(JsonNamingPolicy.SnakeCaseLower).Should().Be(
            new DeadLetterJsonKeys("tenant_id", "projection_name", "shard_name", "exception_type", "timestamp"));
    }

    /// <summary>A soft-deleted mapping - which only a host's own <c>Schema.For&lt;DeadLetterEvent&gt;()</c> makes - keeps Marten's default filter.</summary>
    [Fact]
    public void A_soft_deleted_mapping_keeps_Martens_default_filter()
    {
        DocumentTableInfo softDeleted = Table with
        {
            SoftDeleteEnabled = true,
            MetadataColumns = [new DocumentMetadataColumnInfo(DocumentMetadataColumn.IsSoftDeleted, "mt_deleted")],
        };

        using NpgsqlCommand command = DeadLetterQueries.BuildPage(softDeleted, Keys, new DeadLetterPageQuery());
        using NpgsqlCommand plain = DeadLetterQueries.BuildPage(Table, Keys, new DeadLetterPageQuery());

        command.CommandText.Should().Contain("  and d.\"mt_deleted\" = false\n");
        plain.CommandText.Should().NotContain("mt_deleted");
    }

    /// <summary>
    /// The badge counts what the list shows: one predicate, <see cref="DeadLetterQueries.LiveRows" />, in both statements.
    /// Without it a Discard under soft delete left the badge counting a row the page no longer listed.
    /// </summary>
    [Fact]
    public void The_badge_and_the_list_leave_out_the_same_soft_deleted_rows()
    {
        DocumentTableInfo softDeleted = Settled(Table, withSoftDelete: true);

        using NpgsqlCommand page = DeadLetterQueries.BuildPage(softDeleted, Keys, new DeadLetterPageQuery());
        using NpgsqlCommand count = EventQueryBuilder.BuildDeadLetterCount(softDeleted);
        using NpgsqlCommand plainCount = EventQueryBuilder.BuildDeadLetterCount(Settled(Table, withSoftDelete: false));

        page.CommandText.Should().Contain("  and d.\"mt_deleted\" = false\n");
        count.CommandText.Should().Be("select count(*) from \"studio_events\".\"mt_doc_deadletterevent\" where \"mt_deleted\" = false");
        plainCount.CommandText.Should().Be("select count(*) from \"studio_events\".\"mt_doc_deadletterevent\"");
    }

    /// <summary>
    /// Marten's registration - the fallback for a mapping never materialised - starts as "removed", and the catalog
    /// decides: a table with <c>mt_deleted</c> is soft-deleted however the description began.
    /// </summary>
    [Fact]
    public void The_fallbacks_soft_delete_is_settled_by_the_table_not_assumed()
    {
        DocumentTableInfo registration = DeadLetterQueries.MartensRegistration("studio_events");

        registration.SoftDeleteEnabled.Should().BeFalse("the description's starting point");
        DeadLetterQueries.LiveRows(Settled(registration, withSoftDelete: true), "d").Should().Be("d.\"mt_deleted\" = false");
        DeadLetterQueries.LiveRows(Settled(registration, withSoftDelete: false), "d").Should().BeNull();
    }

    /// <summary>
    /// <c>Policies.AllDocumentsSoftDeleted()</c> reaches <c>DeadLetterEvent</c>: the mapping Marten materialises for it is
    /// soft-deleted, which is the store shape that made the badge and the list disagree.
    /// </summary>
    [Fact]
    public void A_store_wide_soft_delete_policy_reaches_the_dead_letters()
    {
        StoreOptions storeOptions = Store(static options =>
        {
            options.Events.AddEventType<SomethingHappened>();
            options.Policies.AllDocumentsSoftDeleted();
        }).Options;

        DeadLetterQueries.TableFor(storeOptions).SoftDeleteEnabled.Should().BeTrue();
    }

    /// <summary>
    /// The table comes from the mapping Marten materialised - so a host's own <c>Schema.For&lt;DeadLetterEvent&gt;()</c>
    /// counts - and the read never materialises one itself.
    /// </summary>
    [Fact]
    public void The_table_is_the_mapping_Marten_built_including_what_the_host_configured()
    {
        IDocumentStore store = Store(options =>
        {
            options.Events.AddEventType<SomethingHappened>();
            options.Schema.For<DeadLetterEvent>().SoftDeleted();
        });

        DocumentTableInfo table = DeadLetterQueries.TableFor(store.Options);

        table.Schema.Should().Be("studio_dl_events", "Marten registers DeadLetterEvent in the event store's schema");
        table.Table.Should().Be("mt_doc_deadletterevent");
        table.TenancyStyle.Should().Be(TenancyStyle.Single);
        table.SoftDeleteEnabled.Should().BeTrue("the host's own configuration of the type is honoured");
        table.MetadataColumns.Should().NotBeEmpty("this is the mapping's description, not the fallback's");
    }

    /// <summary>
    /// A store whose event store is not active never materialises the mapping (#4303), and the read must not either -
    /// <c>FindOrResolveDocumentType</c> would put a dead-letter table into the host's next migration.
    /// </summary>
    [Fact]
    public void Without_an_active_event_store_the_table_is_Martens_registration_and_no_mapping_is_made()
    {
        IDocumentStore store = Store(static _ => { });

        DocumentTableInfo table = DeadLetterQueries.TableFor(store.Options);

        table.Schema.Should().Be("studio_dl_events");
        table.Table.Should().Be(EventTableInfo.DeadLetterTable);
        table.Alias.Should().Be("deadletterevent");
        table.TenancyStyle.Should().Be(TenancyStyle.Single);
        table.MetadataColumns.Should().BeEmpty("nothing was read from a mapping");

        store.Options.AllKnownDocumentTypes().Should().NotContain(
            static x => x.DocumentType == typeof(DeadLetterEvent), "describing the table taught Marten nothing");
    }

    /// <summary>The table as the catalog would settle it: Marten's columns, with or without the soft-delete pair.</summary>
    private static DocumentTableInfo Settled(DocumentTableInfo table, bool withSoftDelete)
    {
        List<PostgresColumn> columns =
        [
            new("id", "uuid", "uuid", false),
            new("data", "jsonb", "jsonb", false),
            new("mt_last_modified", "timestamp with time zone", "timestamptz", true),
            new("mt_version", "uuid", "uuid", false),
            new("mt_dotnet_type", "character varying", "varchar", true),
        ];

        if (withSoftDelete)
        {
            columns.Add(new PostgresColumn("mt_deleted", "boolean", "bool", true));
            columns.Add(new PostgresColumn("mt_deleted_at", "timestamp with time zone", "timestamptz", true));
        }

        return table.WithPhysicalColumns(new TableColumns(table.Schema, table.Table, columns));
    }

    private static DocumentStore Store(Action<StoreOptions> configure) =>
        DocumentStore.For(options =>
        {
            options.Connection(SqlTestStore.Unreachable);
            options.DatabaseSchemaName = "studio_dl";
            options.Events.DatabaseSchemaName = "studio_dl_events";
            configure(options);
        });

    /// <summary>An event type, which is what makes the event store active.</summary>
    /// <param name="What">Something.</param>
    public sealed record SomethingHappened(string What);
}
