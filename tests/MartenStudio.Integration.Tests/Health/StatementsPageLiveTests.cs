using Marten;

using MartenStudio.SampleDomain.Documents;
using MartenStudio.SampleDomain.Events;
using MartenStudio.Services;
using MartenStudio.Services.Documents;

using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Database;
using PostgresStudio.Services.Statements;

namespace MartenStudio.Integration.Tests.Health;

/// <summary>
/// The Statements page in MartenStudio (W5-Q1), against a real <c>pg_stat_statements</c> on the statement checks' own server:
/// Marten's own SQL grouped by operation and document type - a document upsert, an event append, a load - with a hidden type
/// never named; and the cost meter counting every connection the studio opens - a sweep, a database-browser read and a
/// document read - with C08's rollbacks still exactly the read-only sessions'.
/// </summary>
public class StatementsPageLiveTests(StatementsServerFixture server) : IClassFixture<StatementsServerFixture>
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static void Activity(MartenStudioOptions options)
    {
        options.Capabilities.ViewDatabaseActivity = true;
        options.Capabilities.BrowseDatabase = true;
        options.IsDocumentTypeVisible = static type => type != typeof(StatementHidden);
    }

    private static void Targets(StoreOptions options) => options.Schema.For<StatementHidden>();

    private static async Task<StatementsView> ReadAsync(ConnectionsLiveHost host)
    {
        await using AsyncServiceScope circuit = host.Circuit();
        return await circuit.ServiceProvider.GetRequiredService<IStatementsService>().GetAsync(host.Scope, Token);
    }

    [PostgresFact]
    public async Task Martens_own_statements_are_grouped_by_operation_and_document_type_and_a_hidden_type_is_never_named()
    {
        string database = await StatementsLive.CreateDatabaseAsync(server.ConnectionString, "health_q1_groups");
        await using ConnectionsLiveHost host = await ConnectionsLiveHost.CreateAsync(database, store: Targets, studio: Activity);

        Customer customer = new() { Id = Guid.NewGuid(), Name = "Ada", Email = "ada@example.com" };
        Guid stream = Guid.NewGuid();

        await using (IDocumentSession session = host.Store.LightweightSession())
        {
            session.Store(customer);
            session.Store(new StatementHidden { Id = Guid.NewGuid(), Secret = "hidden" });
            session.Events.StartStream<OrderSummary>(stream, new OrderPlaced(stream, "Ada", DateTimeOffset.UtcNow));
            await session.SaveChangesAsync(Token);
        }

        await using (IDocumentSession session = host.Store.LightweightSession())
        {
            session.Events.Append(stream, new OrderShipped("DHL", DateTimeOffset.UtcNow));
            await session.SaveChangesAsync(Token);
        }

        await using (IQuerySession session = host.Store.QuerySession())
        {
            await session.LoadAsync<Customer>(customer.Id, Token);
            await session.Query<Customer>().Where(static x => x.Name == "Ada").ToListAsync(Token);
            await session.Events.FetchStreamAsync(stream, token: Token);
        }

        StatementsView view = await ReadAsync(host);
        view.State.Should().Be(StatementsState.Read, view.Status);

        // What pg_stat_statements recorded of Marten's SQL, with the group each was given: the classifier's unit tests pin these.
        foreach (StatementRowView row in view.Rows)
        {
            TestContext.Current.TestOutputHelper?.WriteLine("[" + row.GroupLabel + "] " + (row.Text ?? row.TextNote ?? "(no text)"));
        }

        IReadOnlyList<string> groups = [.. view.Rows.Select(static x => x.GroupLabel)];

        groups.Should().Contain("upsert · Customer", "a Store() of a document is Marten's upsert");
        groups.Should().Contain("load/query · Customer", "a LoadAsync and a LINQ query are Marten's reads");
        groups.Should().Contain("event append", "a stream started and appended to is Marten's event append");
        groups.Should().Contain("event read", "FetchStreamAsync reads the event store");

        // Its upsert, and the DDL Marten ran to create its table: every one grouped as a hidden type, none shown.
        List<StatementRowView> hidden = [.. view.Rows.Where(static x => x.GroupLabel == MartenStatementClassifier.Hidden.Label)];
        hidden.Should().NotBeEmpty().And.OnlyContain(static x => x.Text == null, "a hidden type's statement is never shown");
        hidden.Should().Contain(static x => x.TextNote == SessionQueryText.HiddenTypeNote, "its upsert is withheld for naming the hidden type's table");
        groups.Should().NotContain(static x => x.Contains(nameof(StatementHidden), StringComparison.OrdinalIgnoreCase));
        view.Rows.Should().NotContain(static x => (x.Text ?? string.Empty).Contains("statementhidden", StringComparison.OrdinalIgnoreCase));

        host.Logs.WarningsOrWorse.Should().BeEmpty();
    }

    [PostgresFact]
    public async Task The_cost_meter_counts_a_sweep_a_browser_read_and_a_document_read_and_C08s_rollbacks_stay_the_sessions()
    {
        string database = await StatementsLive.CreateDatabaseAsync(server.ConnectionString, "health_q1_meter");
        await using ConnectionsLiveHost host = await ConnectionsLiveHost.CreateAsync(database, studio: Activity);

        string key;
        using (NpgsqlConnection unopened = host.Store.Storage.Database.CreateConnection())
        {
            key = StudioDatabaseTime.KeyOf(unopened);
        }

        StudioDatabaseTime meter = StudioDatabaseTime.Shared;

        long connections = meter.ConnectionsEver(key);
        long rollbacks = meter.RollbacksEver(key);
        long sessions = meter.SessionsEver(key);

        await host.ReportAsync();
        meter.ConnectionsEver(key).Should().BeGreaterThan(connections, "a health sweep's connection is metered");
        (meter.RollbacksEver(key) - rollbacks).Should().Be(meter.SessionsEver(key) - sessions,
            "every read-only session rolls back once, and a connection's own open and close add no rollback - C08 subtracts exactly these");

        connections = meter.ConnectionsEver(key);
        await using (AsyncServiceScope circuit = host.Circuit())
        {
            await circuit.ServiceProvider.GetRequiredService<IDatabaseObjectService>().GetOverviewAsync(host.Scope, Token);
        }

        meter.ConnectionsEver(key).Should().BeGreaterThan(connections, "a database-browser read's connection is metered");

        connections = meter.ConnectionsEver(key);
        await using (AsyncServiceScope circuit = host.Circuit())
        {
            await circuit.ServiceProvider.GetRequiredService<IDocumentDataService>().GetCollectionsAsync(host.Scope, Token);
        }

        meter.ConnectionsEver(key).Should().BeGreaterThan(connections, "a document read's connection is metered - which the meter did not count before 0.5");
        meter.Total(key).Connections.Should().BePositive();
    }
}
