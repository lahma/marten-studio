using JasperFx;

using Marten;
using Marten.Storage;

using MartenStudio.Integration.Tests.Browser;
using MartenStudio.Integration.Tests.Logging;
using MartenStudio.Services;
using MartenStudio.Services.Database;

using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Npgsql;

namespace MartenStudio.Integration.Tests.Database;

/// <summary>
/// DB-3-fix F1, across databases: one circuit reading <c>legacy.things</c> with one filter in each of a
/// multi-database store's two databases is two first contacts - two ring entries and two 9235 lines, each
/// naming its database - where a ledger keyed by the relation's name alone recorded only the first.
/// </summary>
public class TableRowAuditMultiDatabaseLiveTests(TableRowAuditMultiDatabaseLiveTests.Fixture fixture)
    : IClassFixture<TableRowAuditMultiDatabaseLiveTests.Fixture>
{
    /// <summary>Two real databases, each with a <c>legacy.things</c> of its own, and one store over both.</summary>
    public sealed class Fixture(PostgresFixture postgres) : IAsyncLifetime
    {
        private const string Things =
            """
            create schema legacy;
            create table legacy.things (id int primary key, name text);
            insert into legacy.things values (1, 'alpha'), (2, 'banana'), (3, 'cherry');
            """;

        /// <summary>The store over both databases.</summary>
        public DocumentStore Store { get; private set; } = null!;

        /// <summary>The first database.</summary>
        public IMartenDatabase DatabaseA { get; private set; } = null!;

        /// <summary>The second database.</summary>
        public IMartenDatabase DatabaseB { get; private set; } = null!;

        /// <inheritdoc />
        public async ValueTask InitializeAsync()
        {
            if (!DockerAvailability.IsAvailable)
            {
                return;
            }

            string a = await BrowserSuiteFixture.CreateDatabaseAsync(postgres, "table_rows_multi_a");
            string b = await BrowserSuiteFixture.CreateDatabaseAsync(postgres, "table_rows_multi_b");

            foreach (string connectionString in new[] { a, b })
            {
                await using var connection = new NpgsqlConnection(connectionString);
                await connection.OpenAsync();
                await using var command = new NpgsqlCommand(Things, connection);
                await command.ExecuteNonQueryAsync();
            }

            Store = DocumentStore.For(options =>
            {
                options.MultiTenantedDatabases(tenancy =>
                {
                    tenancy.AddSingleTenantDatabase(a, "alpha");
                    tenancy.AddSingleTenantDatabase(b, "beta");
                });

                options.DatabaseSchemaName = "rows_multi";
                options.AutoCreateSchemaObjects = AutoCreate.None;
            });

            IReadOnlyList<IMartenDatabase> databases = await Store.Storage.AllDatabases();
            DatabaseA = databases.Single(static x => x.Id.Name == "table_rows_multi_a");
            DatabaseB = databases.Single(static x => x.Id.Name == "table_rows_multi_b");
        }

        /// <summary>A studio over the store: <c>BrowseDatabase</c> on, <c>legacy</c> browsable.</summary>
        internal RowsHost Host(ILoggerProvider logs)
        {
            var services = new ServiceCollection();

            services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
            services.AddAuthorization();
            services.AddSingleton<AuthenticationStateProvider, TableRowDemoFixture.SignedIn>();
            services.AddSingleton<IDocumentStore>(Store);

            services.AddMartenStudio(options =>
            {
                options.Capabilities.BrowseDatabase = true;
                options.BrowsableSchemas.Add("legacy");
                options.DiscoverTenantIds = false;
            });

            return new RowsHost(services.BuildServiceProvider());
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            if (Store is not null)
            {
                await Store.DisposeAsync();
            }

            GC.SuppressFinalize(this);
        }
    }

    [PostgresFact]
    public async Task The_same_filter_on_the_same_relation_in_another_database_is_a_first_contact_of_its_own()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        var logs = new LogCapture();

        await using RowsHost host = fixture.Host(logs);
        await using AsyncServiceScope circuit = host.Circuit();
        ITableRowService rows = circuit.ServiceProvider.GetRequiredService<ITableRowService>();

        var inA = new StudioScope("default", fixture.DatabaseA.Id.Identity, null);
        var inB = new StudioScope("default", fixture.DatabaseB.Id.Identity, null);
        var request = new TableRowRequest { Filter = "name ~ an" };

        TableRowPage pageA = await rows.ListRowsAsync(inA, "legacy", "things", request, token);
        TableRowPage pageB = await rows.ListRowsAsync(inB, "legacy", "things", request, token);

        pageA.State.Should().Be(TableRowPageState.Loaded, pageA.Reason ?? pageA.Error?.Sentence);
        pageB.State.Should().Be(TableRowPageState.Loaded, pageB.Reason ?? pageB.Error?.Sentence);

        List<StudioActionLogEntry> entries = [.. host.Ring.GetLatest().Where(static x => x.Target == "legacy.things")];

        entries.Should().HaveCount(2, "legacy.things in B is other rows than legacy.things in A");
        entries.Select(static x => x.DatabaseId).Should().BeEquivalentTo([fixture.DatabaseA.Id.Identity, fixture.DatabaseB.Id.Identity]);

        await rows.ListRowsAsync(inA, "legacy", "things", request, token);
        host.Ring.GetLatest().Where(static x => x.Target == "legacy.things").Should().HaveCount(2, "a refresh in A is still A's opening");

        List<StudioLogLine> reads = [.. logs.Lines.Where(static x => x.EventId.Id == 9235)];
        reads.Should().HaveCount(2);
        reads[0].Message.Should().Contain(fixture.DatabaseA.Id.Identity).And.Contain("name ~ an");
        reads[1].Message.Should().Contain(fixture.DatabaseB.Id.Identity).And.Contain("name ~ an");
    }
}
