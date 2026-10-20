using JasperFx.Events.Daemon;

using Marten;

using MartenStudio.Internal.Sql;
using MartenStudio.Services;
using MartenStudio.Services.Configuration;
using MartenStudio.Services.Health;
using MartenStudio.Services.Health.Checks;
using MartenStudio.Tests.Sql;

using Microsoft.Extensions.DependencyInjection;

namespace MartenStudio.Tests.Health;

/// <summary>
/// The CL1 review, item 6: D06's two "not checked" reasons - the host hides <c>DeadLetterEvent</c>, or its delegate throws
/// for it - and the shape statement every daemon and event check shares, which leaves the dead-letter clause out in both
/// cases so a hidden type's table name never goes into a statement.
/// </summary>
/// <remarks>
/// No database: the store's connection string is never opened. A refusal that happens before any statement leaves the
/// sweep's connection unopened (<see cref="HealthConnection.WasOpened" />), which is how "nothing was read" is known.
/// </remarks>
public class DeadLettersVisibilityCheckTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task D06_says_the_host_hides_DeadLetterEvent_and_reads_nothing()
    {
        using Probe probe = Probe.Create(static type => type != typeof(DeadLetterEvent));

        Func<Task> gather = () => new DeadLettersGrowingCheck().GatherAsync(probe.Context, Token);

        (await gather.Should().ThrowAsync<HealthCheckUnavailableException>())
            .Which.Message.Should().Be(DeadLettersGrowingCheck.HiddenReason);
        probe.Connection.WasOpened.Should().BeFalse("no statement ran, the shape included");
    }

    [Fact]
    public async Task D06_says_the_hosts_delegate_threw_and_treats_the_type_as_hidden()
    {
        using Probe probe = Probe.Create(static type => type == typeof(DeadLetterEvent) ? throw new InvalidOperationException("broken") : true);

        Func<Task> gather = () => new DeadLettersGrowingCheck().GatherAsync(probe.Context, Token);

        (await gather.Should().ThrowAsync<HealthCheckUnavailableException>())
            .Which.Message.Should().Be(DeadLettersGrowingCheck.VisibilityFailedReason).And.Contain("threw");
        probe.Connection.WasOpened.Should().BeFalse();
    }

    /// <summary>
    /// The shape every daemon and event check shares asks about the dead-letter table only while the type is shown: hidden
    /// or thrown for, the table is left out, and the statement built from that names no dead-letter table at all.
    /// </summary>
    [Theory]
    [InlineData("hides")]
    [InlineData("throws")]
    public void The_shared_shape_leaves_the_dead_letter_clause_out_while_the_type_is_hidden(string how)
    {
        using Probe probe = Probe.Create(how == "hides"
            ? static type => type != typeof(DeadLetterEvent)
            : static type => type == typeof(DeadLetterEvent) ? throw new InvalidOperationException("broken") : true);

        DocumentTableInfo? deadLetters = EventHealth.ShapeDeadLetters(probe.Context);

        deadLetters.Should().BeNull();

        var query = HealthEventQueries.EventStoreShape(probe.Context.StoreOptions.Events.DatabaseSchemaName, deadLetters?.Schema, deadLetters?.Table);
        query.Sql.Should().NotContain("dead_letter");
        query.Parameters.Select(static x => x.Value).OfType<string>().Should().NotContain(x => x.Contains("deadletterevent", StringComparison.Ordinal));
    }

    /// <summary>The positive control: shown, the shape asks about the table the store maps - the host's alias included.</summary>
    [Fact]
    public void The_shared_shape_asks_about_the_dead_letter_table_the_store_maps_while_the_type_is_shown()
    {
        using Probe probe = Probe.Create(static _ => true, static options => options.Schema.For<DeadLetterEvent>().DocumentAlias("failures"));

        DocumentTableInfo? deadLetters = EventHealth.ShapeDeadLetters(probe.Context);

        deadLetters.Should().NotBeNull();
        deadLetters!.Schema.Should().Be("cl1_d06_events");
        deadLetters.Table.Should().Be("mt_doc_failures");
    }

    /// <summary>A health context over a store with an active event store that is never connected to.</summary>
    private sealed class Probe : IDisposable
    {
        private readonly ServiceProvider services;
        private readonly DocumentStore store;

        private Probe(ServiceProvider services, DocumentStore store, HealthConnection connection, HealthCheckContext context)
        {
            this.services = services;
            this.store = store;
            Connection = connection;
            Context = context;
        }

        public HealthConnection Connection { get; }

        public HealthCheckContext Context { get; }

        public static Probe Create(Func<Type, bool> isVisible, Action<StoreOptions>? configure = null)
        {
            ServiceProvider services = new ServiceCollection().BuildServiceProvider();
            DocumentStore store = DocumentStore.For(options =>
            {
                options.Connection(SqlTestStore.Unreachable);
                options.DatabaseSchemaName = "cl1_d06";
                options.Events.DatabaseSchemaName = "cl1_d06_events";
                options.Events.AddEventType<DeadLetterQueriesTests.SomethingHappened>();
                configure?.Invoke(options);
            });

            HealthConnection connection = new(MartenStudioDatabase.Of(store.Storage.Database));

            HealthCheckContext context = new(
                new ResolvedScope(
                    new StudioScope("default", string.Empty, null),
                    new MartenStoreRegistration("default", "Default", typeof(IDocumentStore)),
                    store,
                    store.Storage.Database),
                MartenStudioSettings.Of(new MartenStudioOptions { IsDocumentTypeVisible = isVisible }),
                MartenStudioRuntime.Current,
                services,
                new HealthSqlRunner(connection, new ReadOnlySqlOptions(), null),
                Now,
                new HealthSweepMemo(),
                CancellationToken.None);

            return new Probe(services, store, connection, context);
        }

        public void Dispose()
        {
            store.Dispose();
            services.Dispose();
        }
    }
}
