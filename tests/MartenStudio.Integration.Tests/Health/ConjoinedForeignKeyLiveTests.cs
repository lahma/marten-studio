using Marten;

using PostgresStudio.Services.Health;

using static MartenStudio.Integration.Tests.Health.TableHealthLive;

namespace MartenStudio.Integration.Tests.Health;

/// <summary>A conjoined document another conjoined type's foreign key points at.</summary>
public sealed class ConjoinedCustomer
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;
}

/// <summary>A conjoined document with a foreign key to <see cref="ConjoinedCustomer" />.</summary>
public sealed class ConjoinedOrder
{
    public Guid Id { get; set; }

    public Guid CustomerId { get; set; }

    public string Note { get; set; } = string.Empty;
}

/// <summary>
/// T15 on Marten's default conjoined foreign key (W5-B1 review F1): <c>ForeignKey&lt;TRef&gt;</c> between two conjoined types
/// keys <c>(customer_id, tenant_id)</c> and indexes <c>customer_id</c> alone, and the lookup a delete runs is a bitmap index
/// scan on it filtered by tenant - not a read of the whole table. So the key is covered, and T15 says nothing.
/// </summary>
public class ConjoinedForeignKeyLiveTests(PostgresFixture postgres)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [PostgresFact]
    public async Task Marten_s_default_conjoined_foreign_key_is_covered_by_its_member_index_and_T15_is_quiet()
    {
        await using HealthLiveHost host = await HostAsync(postgres, "health_t15_conjoined", new ManualClock(Start), static o =>
        {
            o.Schema.For<ConjoinedCustomer>().MultiTenanted();
            o.Schema.For<ConjoinedOrder>().MultiTenanted().ForeignKey<ConjoinedCustomer>(x => x.CustomerId);
        });
        await ApplyAsync(host.Store);

        await using (IDocumentSession session = host.Store.LightweightSession("tenant-a"))
        {
            ConjoinedCustomer customer = new() { Id = Guid.NewGuid(), Name = "Aino" };
            session.Store(customer);
            session.Store(new ConjoinedOrder { Id = Guid.NewGuid(), CustomerId = customer.Id, Note = "the one order every copy is made from" });
            await session.SaveChangesAsync(Token);
        }

        // A quarter of a million copies of that order, so the table is past T15's 8 MiB.
        string orders = TableOf<ConjoinedOrder>(host.Store);
        await ExecuteAsync(
            postgres,
            "do $$ declare cols text; begin "
            + "select pg_catalog.string_agg(pg_catalog.quote_ident(a.attname), ', ' order by a.attnum) into cols from pg_catalog.pg_attribute a "
            + $"where a.attrelid = '{orders}'::pg_catalog.regclass and a.attnum > 0 and not a.attisdropped and a.attname <> 'id'; "
            + $"execute pg_catalog.format('insert into {orders} (id, %s) select gen_random_uuid(), %s from {orders} cross join pg_catalog.generate_series(1, 250000)', cols, cols); "
            + "end $$");
        await ExecuteAsync(postgres, "analyze " + orders);

        // The shape the review measured: a two-column key, and an index that leads with its first column alone.
        (await ScalarAsync($"select pg_catalog.count(*) from pg_catalog.pg_constraint k where k.conrelid = '{orders}'::pg_catalog.regclass and k.contype = 'f' and pg_catalog.array_length(k.conkey, 1) = 2"))
            .Should().Be(1, "ForeignKey<TRef> between conjoined types keys (customer_id, tenant_id)");
        (await ScalarAsync($"select pg_catalog.count(*) from pg_catalog.pg_index x where x.indrelid = '{orders}'::pg_catalog.regclass and x.indnkeyatts = 1 and pg_catalog.pg_get_indexdef(x.indexrelid) like '%(customer_id)%'"))
            .Should().Be(1, "and indexes customer_id alone, TenancyScope.Global being the default");
        (await ScalarAsync($"select pg_catalog.pg_relation_size('{orders}'::pg_catalog.regclass)"))
            .Should().BeGreaterThanOrEqualTo(8L * 1024 * 1024, "the table is one T15 judges");

        HealthReport report = await ReportAsync(host);
        string key = KeyOf<ConjoinedOrder>(host.Store);

        HealthLiveHost.Status(report, "T15").Outcome.Should().Be(HealthCheckOutcome.Checked);
        Of(report, "T15").Where(x => x.Finding.Subject?.StartsWith(key + ".", StringComparison.Ordinal) == true)
            .Should().BeEmpty("tenant_id trails: the index on customer_id serves the lookup, filtered by tenant");
        host.Logs.WarningsOrWorse.Should().BeEmpty();
    }

    private async Task<long> ScalarAsync(string sql)
    {
        await using Npgsql.NpgsqlConnection connection = await postgres.OpenAsync(Token);
        await using Npgsql.NpgsqlCommand command = new(sql, connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync(Token), System.Globalization.CultureInfo.InvariantCulture);
    }
}
