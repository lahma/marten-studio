using JasperFx.Events;
using JasperFx.Events.Aggregation;
using JasperFx.Events.Projections;

using Marten;

using MartenStudio.Internal.Sql;
using MartenStudio.Services.Events;
using MartenStudio.Tests.Sql;

using NpgsqlTypes;

namespace MartenStudio.Tests.Events;

/// <summary>The event that names a ticket, and so sets its natural key.</summary>
/// <param name="TicketId">The stream.</param>
/// <param name="Number">The number people quote.</param>
public sealed record NaturalKeyTicketOpened(Guid TicketId, string Number);

/// <summary>An aggregate whose stream can be found by the number people quote, Marten's own sample shape.</summary>
public sealed class NaturalKeyTicket
{
    /// <summary>The stream id.</summary>
    public Guid Id { get; set; }

    /// <summary>The natural key.</summary>
    [NaturalKey]
    public string Number { get; set; } = string.Empty;

    /// <summary>Sets the key.</summary>
    [NaturalKeySource]
    public void Apply(NaturalKeyTicketOpened opened)
    {
        Id = opened.TicketId;
        Number = opened.Number;
    }
}

/// <summary>
/// F5: which aggregates have a natural key and where Marten keeps it, how a typed value is parsed against
/// it, and the lookup's SQL - no Postgres.
/// </summary>
public class NaturalKeyTests
{
    private static EventTableInfo GuidStore => new()
    {
        Schema = "studio_sql",
        StreamIdentity = StreamIdentity.AsGuid,
        EventColumns = ["seq_id", "id", "stream_id", "version", "data", "type", "timestamp"],
        StreamColumns = ["id", "type", "version", "timestamp", "created", "is_archived", "tenant_id", "compacted_version"],
    };

    [Fact]
    public void An_aggregate_with_a_natural_key_is_found_with_marten_s_table_name()
    {
        IReadOnlyList<NaturalKeyModel> keys = Keys(static options =>
            options.Projections.Snapshot<NaturalKeyTicket>(SnapshotLifecycle.Inline));

        NaturalKeyModel key = keys.Should().ContainSingle().Subject;
        key.AggregateName.Should().Be(nameof(NaturalKeyTicket));
        key.MemberName.Should().Be(nameof(NaturalKeyTicket.Number));
        key.InnerType.Should().Be<string>();
        key.Table.Should().Be("mt_natural_key_naturalkeyticket", "Marten lower-cases the aggregate's own type name");
        key.Label.Should().Be("NaturalKeyTicket.Number");
    }

    [Fact]
    public void A_store_without_natural_keys_has_none_to_look_up()
    {
        Keys(static options => options.Projections.Snapshot<TimeTravelOrder>(SnapshotLifecycle.Inline))
            .Should().BeEmpty();
    }

    [Theory]
    [InlineData(NpgsqlDbType.Integer, typeof(int))]
    [InlineData(NpgsqlDbType.Bigint, typeof(long))]
    [InlineData(NpgsqlDbType.Varchar, typeof(string))]
    [InlineData(NpgsqlDbType.Unknown, typeof(string))]
    public void The_key_is_parsed_as_the_type_its_column_holds(NpgsqlDbType column, Type expected) =>
        NaturalKeyCatalog.ClrTypeFor(column).Should().Be(expected);

    [Fact]
    public void A_word_is_not_looked_up_in_a_number_keyed_table()
    {
        NaturalKeyCatalog.TryParse(typeof(int), "ORD-1", out _).Should().BeFalse();
        NaturalKeyCatalog.TryParse(typeof(long), "12", out object? number).Should().BeTrue();
        number.Should().Be(12L);
        NaturalKeyCatalog.TryParse(typeof(string), "  ORD-1 ", out object? text).Should().BeTrue();
        text.Should().Be("ORD-1");
        NaturalKeyCatalog.TryParse(typeof(string), "   ", out _).Should().BeFalse();
    }

    /// <summary>
    /// Marten's own <c>FetchNaturalKeyPlan</c> shape: a probe on the key that leads the primary key, joined to
    /// <c>mt_streams</c>, both sides narrowed to the tenant, archival read off the stream.
    /// </summary>
    [Fact]
    public void The_lookup_probes_the_key_and_joins_the_stream_in_the_scopes_tenant()
    {
        using var command = NaturalKeyQueries.BuildLookup(GuidStore, new NaturalKeyLookupQuery
        {
            Table = "mt_natural_key_naturalkeyticket",
            Key = "T-100",
            KeyType = NpgsqlDbType.Varchar,
            TableHasTenant = true,
            TenantId = "acme",
            IncludeArchived = false,
        });

        string sql = command.CommandText;

        sql.Should().Contain("from \"studio_sql\".\"mt_natural_key_naturalkeyticket\" as nk");
        sql.Should().Contain("join \"studio_sql\".\"mt_streams\" as s on s.\"id\" = nk.\"stream_id\" and s.\"tenant_id\" = nk.\"tenant_id\"");
        sql.Should().Contain("where nk.\"natural_key_value\" = @key");
        sql.Should().Contain("and (@tenant is null or nk.\"tenant_id\" = @tenant)");
        sql.Should().Contain("and (@includeArchived or s.\"is_archived\" = false)");
        sql.Should().Contain("s.\"compacted_version\"", "a match renders as an ordinary stream row");
        sql.Should().NotContain("T-100").And.NotContain("acme");

        command.Parameters["key"].Value.Should().Be("T-100");
        command.Parameters["key"].NpgsqlDbType.Should().Be(NpgsqlDbType.Varchar);
        command.Parameters["tenant"].Value.Should().Be("acme");
        command.Parameters["includeArchived"].Value.Should().Be(false);
        command.Parameters["limit"].Value.Should().Be(NaturalKeyLookupQuery.MaxRows);
    }

    /// <summary>
    /// The stream's own tenant is the authority, as it is for the stream list: a lookup table without
    /// <c>tenant_id</c> beside a tenanted <c>mt_streams</c> must still never hand another tenant's stream over.
    /// </summary>
    [Fact]
    public void A_lookup_table_without_a_tenant_column_is_still_narrowed_by_the_streams_own_tenant()
    {
        using var command = NaturalKeyQueries.BuildLookup(GuidStore, new NaturalKeyLookupQuery
        {
            Table = "mt_natural_key_naturalkeyticket",
            Key = "T-100",
            KeyType = NpgsqlDbType.Varchar,
            TableHasTenant = false,
            TenantId = "acme",
        });

        string sql = command.CommandText;

        sql.Should().Contain("and (@tenant is null or s.\"tenant_id\" = @tenant)");
        sql.Should().NotContain("nk.\"tenant_id\"", "the lookup table has no such column");
        command.Parameters["tenant"].Value.Should().Be("acme");
    }

    [Fact]
    public void On_a_conjoined_store_both_sides_are_narrowed_to_the_tenant()
    {
        using var command = NaturalKeyQueries.BuildLookup(GuidStore, new NaturalKeyLookupQuery
        {
            Table = "mt_natural_key_naturalkeyticket",
            Key = "T-100",
            TableHasTenant = true,
            TenantId = "acme",
        });

        command.CommandText.Should().Contain("and (@tenant is null or nk.\"tenant_id\" = @tenant)")
            .And.Contain("and (@tenant is null or s.\"tenant_id\" = @tenant)");
    }

    /// <summary>A hidden aggregate's natural key is not offered, not named in the placeholder and not looked up.</summary>
    [Fact]
    public void An_aggregate_the_host_hides_has_no_natural_key_here()
    {
        DocumentStore store = DocumentStore.For(options =>
        {
            options.Connection(SqlTestStore.Unreachable);
            options.DatabaseSchemaName = SqlTestStore.Schema;
            options.Projections.Snapshot<NaturalKeyTicket>(SnapshotLifecycle.Inline);
        });

        NaturalKeyCatalog.From(store.Options, static type => type != typeof(NaturalKeyTicket)).Should().BeEmpty();
        NaturalKeyCatalog.From(store.Options, static _ => true).Should().ContainSingle();
    }

    [Fact]
    public void A_string_identified_store_joins_on_the_stream_key_and_a_single_tenanted_one_names_no_tenant()
    {
        EventTableInfo stringStore = GuidStore with
        {
            StreamIdentity = StreamIdentity.AsString,
            StreamColumns = ["id", "type", "version", "timestamp", "created", "is_archived"],
        };

        using var command = NaturalKeyQueries.BuildLookup(stringStore, new NaturalKeyLookupQuery
        {
            Table = "mt_natural_key_naturalkeyticket",
            Key = 42,
            KeyType = NpgsqlDbType.Integer,
        });

        command.CommandText.Should().Contain("on s.\"id\" = nk.\"stream_key\"");
        command.CommandText.Should().NotContain("tenant_id");
        command.Parameters.Contains("tenant").Should().BeFalse();
    }

    private static IReadOnlyList<NaturalKeyModel> Keys(Action<StoreOptions> configure)
    {
        IDocumentStore store = DocumentStore.For(options =>
        {
            options.Connection(SqlTestStore.Unreachable);
            options.DatabaseSchemaName = SqlTestStore.Schema;
            configure(options);
        });

        return NaturalKeyCatalog.From(store.Options);
    }
}
