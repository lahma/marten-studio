using System.Text.Json;

using Marten;

using MartenStudio.Services;
using MartenStudio.Services.Connection;

using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using PostgresStudio.Services;
using PostgresStudio.Services.Connection;
using PostgresStudio.Services.Database;

namespace MartenStudio.Tests.Connection;

/// <summary>
/// "How Marten uses this connection" (1.1-NP4): the real contributor, built by <c>AddMartenStudio()</c>, over real Marten stores that
/// never connect. The command timeout is Marten's resolution or an honest pair of figures, the data source's origin is a reference
/// comparison over the container's registrations, the hints get Marten's sentence, and no connection string reaches any of it.
/// </summary>
public class MartenConnectionInsightTests
{
    private const string Sentinel = "S3NT1NEL";

    private const string Remote = "Host=marten-np4.invalid;Port=5432;Database=np4;Username=svc";

    private static readonly StudioScope Scope = new("default", string.Empty, null);

    private static readonly JsonSerializerOptions DumpOptions = new() { IncludeFields = true, MaxDepth = 64 };

    // ----------------------------------------------------------------------------------------------
    // The command timeout
    // ----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(30, null, "StoreOption", 30)]
    [InlineData(45, 120, "StoreOption", 45)]
    [InlineData(5, null, "Default", 5)]
    [InlineData(5, 5, "Agreed", 5)]
    [InlineData(5, 120, "Undetermined", null)]
    public void The_timeout_is_resolved_where_public_members_can_prove_it_and_is_not_claimed_where_they_cannot(int store, int? named, string basis, int? effective)
    {
        MartenCommandTimeout timeout = new(store, named);

        timeout.Basis.ToString().Should().Be(basis);
        timeout.EffectiveSeconds.Should().Be(effective);
    }

    [Fact]
    public void An_undetermined_timeout_shows_both_numbers_and_says_which_one_wins_on_each_reading()
    {
        MartenCommandTimeout timeout = new(5, 120);

        timeout.Value.Should().Be("5 s or 120 s");
        timeout.Explanation.Should().Contain("StoreOptions.CommandTimeout is 5 s")
            .And.Contain("names a Command Timeout of 120 s")
            .And.Contain("120 s if StoreOptions.CommandTimeout was left alone")
            .And.Contain("5 s if it was set to 5 s on purpose")
            .And.Contain("does not publish which")
            .And.Contain("SessionOptions.Timeout");
    }

    [Fact]
    public void A_default_timeout_is_marten_s_five_seconds_and_not_npgsql_s_thirty()
    {
        new MartenCommandTimeout(5, null).Explanation.Should().Contain("Nothing sets it").And.Contain("5 s, not Npgsql's 30 s");
    }

    [Fact]
    public async Task A_store_that_connects_with_a_string_runs_at_the_strings_timeout_which_Marten_stored_into_the_option()
    {
        await using Rig rig = Rig.Create(static options => options.Connection(Remote + ";Command Timeout=77"));

        ConnectionContribution contribution = await rig.ContributeAsync();
        ConnectionObservedLine row = Row(contribution, "Command timeout");

        row.Value.Should().Be("77 s");
        row.Note.Should().Contain("StoreOptions.CommandTimeout is 77 s").And.Contain("names a Command Timeout of 77 s");
    }

    [Fact]
    public async Task A_string_that_names_no_timeout_runs_at_npgsql_s_thirty_because_Marten_stores_the_builders_default()
    {
        await using Rig rig = Rig.Create(static options => options.Connection(Remote));

        ConnectionObservedLine row = Row(await rig.ContributeAsync(), "Command timeout");

        row.Value.Should().Be("30 s");
        row.Note.Should().Contain("names no Command Timeout");
    }

    [Fact]
    public async Task A_command_timeout_set_on_purpose_wins_over_the_connection_string()
    {
        await using Rig rig = Rig.Create(static options =>
        {
            options.Connection(Remote + ";Command Timeout=77");
            options.CommandTimeout = 12;
        });

        ConnectionObservedLine row = Row(await rig.ContributeAsync(), "Command timeout");

        row.Value.Should().Be("12 s");
        row.Note.Should().Contain("was set on purpose, so it wins");
    }

    // ----------------------------------------------------------------------------------------------
    // The data source's origin
    // ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_store_that_uses_the_registered_data_source_says_so_and_points_at_C18()
    {
        await using Rig rig = Rig.Create(
            static _ => { },
            static (services, marten) =>
            {
                services.AddSingleton(NpgsqlDataSource.Create(Remote));
                marten.UseNpgsqlDataSource();
            });

        ConnectionObservedLine row = Row(await rig.ContributeAsync(), "Data source");

        row.Value.Should().Be("the application's NpgsqlDataSource");
        row.Note.Should().Contain("draw on one pool").And.Contain("C18");
    }

    [Fact]
    public async Task A_keyed_data_source_is_found_and_its_key_is_named()
    {
        await using Rig rig = Rig.Create(
            static _ => { },
            static (services, marten) =>
            {
                services.AddKeyedSingleton("analytics", NpgsqlDataSource.Create(Remote));
                marten.UseNpgsqlDataSource("analytics");
            });

        Row(await rig.ContributeAsync(), "Data source").Value.Should().Be("the application's NpgsqlDataSource (service key analytics)");
    }

    [Fact]
    public async Task A_store_that_connects_with_a_string_has_a_pool_of_its_own_and_is_told_what_else_the_application_registers()
    {
        await using Rig alone = Rig.Create(static options => options.Connection(Remote));
        ConnectionContribution none = await alone.ContributeAsync();

        Row(none, "Data source").Value.Should().Be("Marten's own");
        Row(none, "Data source").Note.Should().Contain("registers no NpgsqlDataSource of its own");
        none.Sections.Single().Cards.Should().BeEmpty("there is nothing to share a pool with");

        await using Rig beside = Rig.Create(
            static options => options.Connection(Remote),
            static (services, _) => services.AddSingleton(NpgsqlDataSource.Create(Remote + "2")));
        ConnectionContribution two = await beside.ContributeAsync();

        Row(two, "Data source").Note.Should().Contain("registers 1 NpgsqlDataSource").And.Contain("C18").And.Contain("UseNpgsqlDataSource()");

        ConnectionCard card = two.Sections.Single().Cards.Should().ContainSingle().Which;
        card.Heading.Should().Contain("pool of its own");
        card.Fixes.Should().ContainSingle().Which.Code.Should().Contain(".UseNpgsqlDataSource();");
        two.Cards.Should().BeEmpty("a possible second pool is context, not a thing the page counts");
    }

    [Fact]
    public void Only_a_singleton_registration_can_be_the_one_marten_was_handed()
    {
        ServiceCollection services = new();
        NpgsqlDataSource singleton = NpgsqlDataSource.Create(Remote);
        services.AddSingleton(singleton);
        services.AddScoped(static _ => NpgsqlDataSource.Create(Remote + "-scoped"));
        services.AddKeyedTransient<NpgsqlDataSource>("t", static (_, _) => NpgsqlDataSource.Create(Remote + "-transient"));

        using ServiceProvider provider = services.BuildServiceProvider();

        // The last unkeyed descriptor is the scoped one, which is what the container resolves, and it is not Marten's.
        new MartenDataSourceRegistry(services).Registered(provider).Should().BeEmpty();

        ServiceCollection singletonLast = new();
        singletonLast.AddScoped(static _ => NpgsqlDataSource.Create(Remote + "-scoped"));
        singletonLast.AddSingleton(singleton);

        using ServiceProvider second = singletonLast.BuildServiceProvider();
        new MartenDataSourceRegistry(singletonLast).Registered(second).Should().ContainSingle().Which.DataSource.Should().BeSameAs(singleton);
    }

    [Fact]
    public void A_database_with_no_data_source_to_compare_is_not_known_rather_than_marten_s_own()
    {
        MartenDataSourceOrigin.Of(null, []).Kind.Should().Be(MartenDataSourceOriginKind.Unknown);
    }

    // ----------------------------------------------------------------------------------------------
    // The options, each with its StoreOptions line and its default
    // ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Every_connection_option_has_its_StoreOptions_line_and_its_default()
    {
        await using Rig rig = Rig.Create(static options =>
        {
            options.Connection(Remote);
            options.UseStickyConnectionLifetimes = true;
            options.DisableNpgsqlLogging = false;
            options.UpdateBatchSize = 250;
            options.Advanced.HiloSequenceDefaults.MaxLo = 100;
            options.OpenTelemetry.TrackConnections = JasperFx.OpenTelemetry.TrackLevel.Normal;
        });

        ConnectionContribution contribution = await rig.ContributeAsync();

        ConnectionObservedLine sticky = Row(contribution, "Sticky connections");
        sticky.Value.Should().Be("on");
        sticky.Note.Should().Contain("StoreOptions.UseStickyConnectionLifetimes, default off").And.Contain("pool of 50").And.Contain("C01");

        Row(contribution, "Npgsql logging").Value.Should().Be("enabled");
        Row(contribution, "Npgsql logging").Note.Should().Contain("StoreOptions.DisableNpgsqlLogging, default true");
        Row(contribution, "Connection tracing").Value.Should().Be("Normal");
        Row(contribution, "Connection tracing").Note.Should().Contain("StoreOptions.OpenTelemetry.TrackConnections, default None");
        Row(contribution, "Hi-lo block size").Value.Should().Be("100");
        Row(contribution, "Hi-lo block size").Note.Should().Contain("StoreOptions.Advanced.HiloSequenceDefaults.MaxLo, default 1,000");
        Row(contribution, "Update batch size").Value.Should().Be("250");
        Row(contribution, "Update batch size").Note.Should().Contain("StoreOptions.UpdateBatchSize, default 500");
    }

    [Fact]
    public async Task A_single_host_data_source_ignores_the_multi_host_preferences_and_says_so()
    {
        await using Rig rig = Rig.Create(static options =>
        {
            options.Connection(Remote);
            options.Advanced.MultiHostSettings.ReadSessionPreference = TargetSessionAttributes.PreferStandby;
        });

        ConnectionObservedLine row = Row(await rig.ContributeAsync(), "Session preference on several hosts");

        row.Value.Should().Be("reads: PreferStandby, writes: ReadWrite");
        row.Note.Should().Contain("StoreOptions.Advanced.MultiHostSettings.ReadSessionPreference and WriteSessionPreference, both default ReadWrite")
            .And.Contain("names one host, so they have no effect here");
    }

    [Fact]
    public async Task A_multi_host_data_source_uses_the_preferences_and_a_write_preference_that_can_reach_a_standby_is_called_out()
    {
        await using Rig rig = Rig.Create(
            static options => options.Advanced.MultiHostSettings.WriteSessionPreference = TargetSessionAttributes.Any,
            static (services, marten) =>
            {
                services.AddSingleton<NpgsqlDataSource>(new NpgsqlDataSourceBuilder("Host=a.invalid,b.invalid;Database=np4;Username=svc").BuildMultiHost());
                marten.UseNpgsqlDataSource();
            });

        ConnectionContribution contribution = await rig.ContributeAsync();

        Row(contribution, "Session preference on several hosts").Note.Should().Contain("so these choose the host of each session")
            .And.Contain("WriteSessionPreference Any can send a write to a standby");
        contribution.HintNotes[ConnectionHintIds.MultiHost].Should().Contain("reads ReadWrite and writes Any")
            .And.Contain("can send a write to a standby: set WriteSessionPreference to ReadWrite or Primary");
    }

    [Fact]
    public async Task The_default_preference_sends_marten_s_writes_to_a_primary_whatever_the_connection_string_accepts()
    {
        await using Rig rig = Rig.Create(
            static _ => { },
            static (services, marten) =>
            {
                services.AddSingleton<NpgsqlDataSource>(new NpgsqlDataSourceBuilder("Host=a.invalid,b.invalid;Database=np4;Username=svc").BuildMultiHost());
                marten.UseNpgsqlDataSource();
            });

        (await rig.ContributeAsync()).HintNotes[ConnectionHintIds.MultiHost].Should().Contain("Marten's writes go to a primary even though this connection string would accept any host");
    }

    [Fact]
    public async Task The_single_host_hint_note_says_the_preferences_do_not_apply()
    {
        await using Rig rig = Rig.Create(static options => options.Connection(Remote));

        (await rig.ContributeAsync()).HintNotes[ConnectionHintIds.MultiHost].Should().Contain("applies only to a multi-host data source");
    }

    [Fact]
    public async Task The_command_timeout_hint_says_the_timeout_that_counts_is_marten_s_and_who_cancels_first()
    {
        await using Rig rig = Rig.Create(static options =>
        {
            options.Connection(Remote);
            options.CommandTimeout = 60;
        });

        ConnectionContribution server = await rig.ContributeAsync(observed: Observed(statementTimeoutMs: 10_000));
        server.HintNotes[ConnectionHintIds.CommandTimeout].Should().Contain("the figure that counts is Marten's, 60 s")
            .And.Contain("not Npgsql's Command Timeout")
            .And.Contain("the server cancels a long Marten command first");

        ConnectionContribution marten = await rig.ContributeAsync(observed: Observed(statementTimeoutMs: 120_000));
        marten.HintNotes[ConnectionHintIds.CommandTimeout].Should().Contain("Marten's timeout cancels a long command first");

        ConnectionContribution unknown = await rig.ContributeAsync(observed: null);
        unknown.HintNotes[ConnectionHintIds.CommandTimeout].Should().NotContain("cancels a long");
    }

    // ----------------------------------------------------------------------------------------------
    // The daemon
    // ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Before_the_sweep_has_looked_the_daemon_is_not_known_yet_and_nothing_is_started_to_find_out()
    {
        await using Rig rig = Rig.Create(static options => options.Connection(Remote));

        ConnectionObservedLine row = Row(await rig.ContributeAsync(), "Async daemon");

        row.Value.Should().Be("not known yet");
        row.Note.Should().Contain("C09");
    }

    [Fact]
    public void A_hosted_daemon_needs_one_connection_for_itself_and_one_per_running_shard()
    {
        MartenDaemonNeeds needs = new(true, null, 4, 6);
        needs.Connections.Should().Be(5);

        ConnectionObservedLine row = Row(MartenConnectionSection.Build(Options(), Own(), needs, poolMax: 100));

        row.Value.Should().Be("4 shards running: about 5 connections");
        row.Note.Should().Contain("One connection for the daemon itself").And.Contain("plus one per running shard")
            .And.Contain("about 5 of 100").And.Contain("registers 6 async shards in all").And.Contain("C09");
    }

    [Fact]
    public void A_daemon_that_is_not_here_says_why_and_a_single_shard_is_singular()
    {
        Row(MartenConnectionSection.Build(Options(), Own(), new MartenDaemonNeeds(false, "No async daemon is hosted in this process for this store.", 0, null), 100))
            .Value.Should().Be("not hosted in this process");

        Row(MartenConnectionSection.Build(Options(), Own(), new MartenDaemonNeeds(true, null, 1, null), null))
            .Value.Should().Be("1 shard running: about 2 connections");
    }

    // ----------------------------------------------------------------------------------------------
    // The BUFFER evidence
    // ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_average_size_the_deeper_checks_measured_is_handed_to_the_page_for_the_buffer_hint()
    {
        await using Rig rig = Rig.Create(static options => options.Connection(Remote));

        (await rig.ContributeAsync()).AverageRowBytes.Should().BeNull("no deeper check has run");

        rig.RowSizes.Record(rig.Scope.Source.Key, rig.Scope.Target.Identity, MartenRowSizeCache.Events, 12_000);
        rig.RowSizes.Record(rig.Scope.Source.Key, rig.Scope.Target.Identity, MartenRowSizeCache.Documents, 20_000);

        ConnectionContribution contribution = await rig.ContributeAsync();
        contribution.AverageRowBytes.Should().Be(20_000, "the largest typical size is the one a read buffer has to fit");

        // ...and it reaches the BUFFER hint on the page, with a Read Buffer Size to copy.
        ConnectionFacts facts = Facts(Remote);
        ConnectionPageView view = ConnectionPageBuilder.Build(new ConnectionPageInputs
        {
            Facts = facts,
            Contributions = [contribution],
            Activity = new SessionDetailsAccess(false, false, null, DatabaseRefusal.CapabilityOff, null),
            MeasuredAt = DateTimeOffset.UnixEpoch,
        });

        ConnectionCard buffer = view.Cards.Single(static x => x.Code == ConnectionHintIds.Buffer);
        buffer.Text.Should().Contain("20000 bytes").And.Contain("Read Buffer Size (8192)");
        buffer.Fixes.Should().Contain(static x => x.Label == "Connection string" && x.Code == "Read Buffer Size=22000");
    }

    [Fact]
    public void A_figure_is_only_the_deeper_sweeps_for_as_long_as_the_sweeps_results_are_shown()
    {
        MovableClock clock = new(new DateTimeOffset(2026, 11, 3, 9, 0, 0, TimeSpan.Zero));
        MartenRowSizeCache cache = new(clock: clock);

        cache.Record("default", "db", MartenRowSizeCache.Events, 9_000);
        cache.Average("default", "db").Should().Be(9_000);

        clock.Advance(TimeSpan.FromMinutes(4));
        cache.Average("default", "db").Should().Be(9_000);

        clock.Advance(TimeSpan.FromMinutes(2));
        cache.Average("default", "db").Should().BeNull("the deeper results this came from are no longer shown");

        // A check with too little to judge withdraws its figure.
        cache.Record("default", "db", MartenRowSizeCache.Events, 9_000);
        cache.Record("default", "db", MartenRowSizeCache.Events, null);
        cache.Average("default", "db").Should().BeNull();
        cache.DatabaseCount.Should().Be(0);

        cache.Record("default", "db", MartenRowSizeCache.Documents, 5);
        cache.Forget("default", "db");
        cache.Average("default", "db").Should().BeNull();
    }

    [Fact]
    public void An_events_window_is_averaged_only_when_it_is_big_enough_for_E04_to_judge()
    {
        MartenRowSizeCache.AverageEventBytes(null).Should().BeNull();
        MartenRowSizeCache.AverageEventBytes(Traffic(events: 99, bytes: 99_000)).Should().BeNull();
        MartenRowSizeCache.AverageEventBytes(Traffic(events: 200, bytes: 2_000_000)).Should().Be(10_000);
    }

    // ----------------------------------------------------------------------------------------------
    // Redaction (NP-DESIGN section 2)
    // ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task No_string_of_the_section_the_hint_notes_or_the_configuration_rows_carries_a_secret_of_the_connection_string()
    {
        string connection = "Host=marten-np4.invalid;Port=5432;Database=np4;Username=svc;Password=" + Sentinel + "-password;SSL Password=" + Sentinel + "-sslpassword"
            + ";Passfile=/tmp/" + Sentinel + "-passfile;Options=-c app.secret=" + Sentinel + "-opt;Command Timeout=77;Application Name=" + Sentinel + "-app";

        await using Rig rig = Rig.Create(options =>
        {
            options.Connection(connection);
            options.UseRowLevelSecurity("app." + Sentinel + "_tenant");
            options.Schema.For<Note>();
        });

        ConnectionContribution contribution = await rig.ContributeAsync(observed: Observed(statementTimeoutMs: 10_000));
        string dump = JsonSerializer.Serialize(contribution, DumpOptions);

        dump.Should().Contain("Command timeout", "a vacuous grep is a broken test");
        dump.Should().NotContain(Sentinel);

        // The Configuration page's rows are made from the same figures.
        IReadOnlyList<MartenStudio.Services.Configuration.ConfigurationValue> rows =
            MartenStudio.Services.Configuration.ConfigurationDescriber.DescribeConnections(rig.StoreOptions, rig.Scope.Database);

        rows.Should().Contain(static x => x.Name == "Row-level security" && x.Value == "configured", "configured is all the page says about the setting");
        string rowsDump = JsonSerializer.Serialize(rows, DumpOptions);
        rowsDump.Should().Contain("77 s");
        rowsDump.Should().NotContain(Sentinel);

        // Nothing in the whole dump is a connection string.
        (dump + rowsDump).Should().NotContainEquivalentOf("password=").And.NotContain("Host=");
    }

    // ----------------------------------------------------------------------------------------------
    // The seam
    // ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_section_is_one_titled_block_of_rows_and_never_a_counted_card_of_its_own()
    {
        await using Rig rig = Rig.Create(static options => options.Connection(Remote));

        ConnectionContribution contribution = await rig.ContributeAsync();
        ConnectionContributedSection section = contribution.Sections.Should().ContainSingle().Which;

        section.Id.Should().Be("conn-marten");
        section.Title.Should().Be("How Marten uses this connection");
        section.Rows.Select(static x => x.Label).Should().Equal(
            "Data source", "Command timeout", "Sticky connections", "Session preference on several hosts", "Async daemon",
            "Hi-lo block size", "Update batch size", "Npgsql logging", "Connection tracing");
        section.Rows.Should().OnlyContain(static x => x.Note != null && x.Note.Length > 0, "every row says what it is and what to change");
        contribution.Cards.Should().BeEmpty();
    }

    // ----------------------------------------------------------------------------------------------
    // Helpers
    // ----------------------------------------------------------------------------------------------

    private static ConnectionObservedLine Row(ConnectionContribution contribution, string label) =>
        contribution.Sections.Single().Rows.Single(x => x.Label == label);

    private static ConnectionObservedLine Row(ConnectionContributedSection section, string label = "Async daemon") =>
        section.Rows.Single(x => x.Label == label);

    private static MartenConnectionOptions Options() => new(
        new MartenCommandTimeout(30, null), false, TargetSessionAttributes.ReadWrite, TargetSessionAttributes.ReadWrite, true, "None", 1000, 30, 500, 4004, false, true, true);

    private static MartenDataSourceOrigin Own() => new(MartenDataSourceOriginKind.Marten, null, 0, false);

    internal static ConnectionFacts Facts(string connectionString, ConnectionObserved? observed = null) => new()
    {
        Settings = NpgsqlSettingsReader.Read(connectionString + ";Persist Security Info=false", new ConnectionReadContext()),
        Observed = observed ?? Observed(),
    };

    internal static ConnectionObserved Observed(long statementTimeoutMs = 0) => new(
        "svc", "svc", "np4", "10.0.0.5", 5432, false, "UTC", "UTF8", "public", "PostgresStudio",
        0, statementTimeoutMs, 0, null, "scram-sha-256", true, "TLSv1.3", "TLS_AES_256_GCM_SHA384", 256, null, null,
        new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    private static MartenStudio.Services.Health.Checks.TrafficProfile Traffic(long events, long bytes) => new(
        new MartenStudio.Internal.Sql.TrafficProfileRow("total", null, null, events, bytes, null, null, null, null, null, null, null, null, null, null),
        [], [], [], "tombstone", JasperFx.Events.EventAppendMode.Quick, false);

    /// <summary>A real container over a real store that never connects, with the Marten contributor AddMartenStudio registered.</summary>
    internal sealed class Rig : IAsyncDisposable
    {
        private readonly ServiceProvider provider;
        private readonly AsyncServiceScope circuit;

        private Rig(ServiceProvider provider)
        {
            this.provider = provider;
            circuit = provider.CreateAsyncScope();
            Store = provider.GetRequiredService<IDocumentStore>();
            Scope = new ResolvedScope(
                new StudioScope("default", string.Empty, null),
                new MartenStoreRegistration("default", "Default", typeof(IDocumentStore)),
                Store,
                Store.Storage.Database);
        }

        public IDocumentStore Store { get; }

        public StoreOptions StoreOptions => (StoreOptions) Store.Options;

        public ResolvedScope Scope { get; }

        public MartenRowSizeCache RowSizes => provider.GetRequiredService<MartenRowSizeCache>();

        public static Rig Create(
            Action<StoreOptions> store,
            Action<IServiceCollection, MartenServiceCollectionExtensions.MartenConfigurationExpression>? services = null)
        {
            ServiceCollection collection = new();
            collection.AddLogging();

            MartenServiceCollectionExtensions.MartenConfigurationExpression marten = collection.AddMarten(options =>
            {
                options.DatabaseSchemaName = "np4_unit";
                store(options);
            });

            services?.Invoke(collection, marten);
            collection.AddMartenStudio();

            return new Rig(collection.BuildServiceProvider());
        }

        public Task<ConnectionContribution> ContributeAsync(ConnectionObserved? observed = null)
        {
            ConnectionContributorContext context = new(
                Scope,
                NpgsqlSettingsReader.Read(Remote + ";Maximum Pool Size=50;Persist Security Info=false", new ConnectionReadContext()),
                observed);

            MartenConnectionInsightContributor contributor = circuit.ServiceProvider.GetServices<IConnectionInsightContributor>().OfType<MartenConnectionInsightContributor>().Single();

            return contributor.ContributeAsync(context, Xunit.TestContext.Current.CancellationToken)
                .ContinueWith(static task => task.Result ?? throw new InvalidOperationException("The contributor answered nothing."), TaskScheduler.Default);
        }

        public async ValueTask DisposeAsync()
        {
            await circuit.DisposeAsync();
            await provider.DisposeAsync();
        }
    }

    private sealed class MovableClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset now = start;

        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan by) => now = now.Add(by);
    }

    private sealed class Note
    {
        public Guid Id { get; set; }
    }
}
