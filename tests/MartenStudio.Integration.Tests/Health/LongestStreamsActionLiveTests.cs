using System.Security.Claims;

using JasperFx;
using JasperFx.Events.Projections;
using JasperFx.MultiTenancy;

using Marten;

using MartenStudio.Integration.Tests.Documents;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using PostgresStudio.Internal.Sql;
using PostgresStudio.Services;
using PostgresStudio.Services.Health;

namespace MartenStudio.Integration.Tests.Health;

/// <summary>An aggregate the test store keeps in an inline snapshot: its streams are not rebuilt on each fetch.</summary>
public sealed class LongKept
{
    /// <summary>The stream's id.</summary>
    public Guid Id { get; set; }

    /// <summary>How many pings it has seen.</summary>
    public int Count { get; set; }

    /// <summary>Counts a ping.</summary>
    public void Apply(HealthPing ping) => Count++;
}

/// <summary>An aggregate the test host hides from the studio.</summary>
public sealed class LongSecret
{
    /// <summary>The stream's id.</summary>
    public Guid Id { get; set; }

    /// <summary>How many pings it has seen.</summary>
    public int Count { get; set; }

    /// <summary>Counts a ping.</summary>
    public void Apply(HealthPing ping) => Count++;
}

/// <summary>
/// E06's "Find the 20 longest streams" (1.2-H-C) against a real event store: the order, the limit, what is left out, how an
/// aggregate is told apart, a conjoined store's reach, a hidden type's absence, a lock, a role without <c>SELECT</c>, and what
/// the audit ring is left holding.
/// </summary>
public class LongestStreamsActionLiveTests(PostgresFixture postgres)
{
    private const string Action = "e06.longest-streams";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // ---------------------------------------------------------------------------------------------------
    // The statement
    // ---------------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task The_twenty_longest_live_streams_are_listed_longest_first_with_links_and_archived_ones_are_left_out()
    {
        await using LongestStreamsHost host = await LongestStreamsHost.CreateAsync(postgres, "lsa_order");

        for (int events = 1; events <= 25; events++)
        {
            await host.StartAsync<HealthCounter>(events);
        }

        Guid archived = await host.StartAsync<HealthCounter>(100);
        await host.ArchiveAsync(archived);

        HealthActionResult result = await host.PressAsync();

        result.Outcome.Should().Be(HealthActionOutcome.Done, result.Reason);
        result.Rows.Should().HaveCount(20, "the limit is twenty, whatever the store holds");
        result.Rows.Select(static x => x.Cells[1].Text).Should().Equal(Enumerable.Range(6, 20).Reverse().Select(static x => x.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)));
        result.Rows.SelectMany(static x => x.Cells).Select(static x => x.Text).Should().NotContain(archived.ToString(), "an archived stream is not live");
        result.Notes.Should().Contain("Archived streams are left out.");

        HealthActionCell first = result.Rows[0].Cells[0];
        first.Link!.Route.Should().Be("events/streams/s");
        first.Link.Query.Should().ContainSingle().Which.Value.Should().Be(first.Text);
        Guid.TryParse(first.Text, out _).Should().BeTrue();

        result.Statements.Should().ContainSingle().Which.Sql.Should().Contain("mt_streams").And.NotContain("mt_events");
        result.Took.Should().NotBeNull();
    }

    [PostgresFact]
    public async Task Each_stream_is_told_apart_by_the_aggregate_it_was_started_as_and_how_that_aggregate_is_fetched()
    {
        await using LongestStreamsHost host = await LongestStreamsHost.CreateAsync(postgres, "lsa_classes");

        await host.StartAsync(60);
        await host.StartAsync<HealthCounter>(50);
        await host.StartAsync<LongKept>(45);
        await host.StartAsync<HealthCounter>(40);

        HealthActionResult result = await host.PressAsync();

        result.Rows.Select(static x => (x.Cells[1].Text, x.Cells[2].Text, x.Cells[3].Text)).Should().Equal(
            ("60", "not recorded", "not an aggregate this process knows"),
            ("50", "HealthCounter", "rebuilt from every event"),
            ("45", "LongKept", "kept by a projection"),
            ("40", "HealthCounter", "rebuilt from every event"));

        result.Summary.Should().Be(
            "The 4 longest live streams in this database: 1 with no type; 2 were started as HealthCounter, rebuilt from every event on each fetch; "
            + "1 was started as LongKept, kept by a projection.");

        result.Remedies.Should().ContainSingle().Which.Code.Should().Be("opts.Projections.Snapshot<HealthCounter>(SnapshotLifecycle.Inline);");
    }

    [PostgresFact]
    public async Task A_stream_over_a_thousand_events_is_counted_in_the_summary()
    {
        await using LongestStreamsHost host = await LongestStreamsHost.CreateAsync(postgres, "lsa_thousand");

        await host.StartAsync<HealthCounter>(1_200);
        await host.StartAsync<HealthCounter>(10);

        HealthActionResult result = await host.PressAsync();

        result.Summary.Should().EndWith("1 is over 1,000 events.");
        result.Rows[0].Cells[1].Text.Should().Be("1,200");
    }

    // ---------------------------------------------------------------------------------------------------
    // Reach (D27)
    // ---------------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task On_a_conjoined_store_a_selected_tenant_reads_only_its_own_streams()
    {
        await using LongestStreamsHost host = await LongestStreamsHost.CreateAsync(postgres, "lsa_tenant", conjoined: true);

        await host.StartAsync<HealthCounter>(12, "acme");
        await host.StartAsync<HealthCounter>(10, "acme");
        await host.StartAsync<HealthCounter>(30, "globex");

        HealthActionResult result = await host.PressAsync(tenant: "acme");

        result.Outcome.Should().Be(HealthActionOutcome.Done, result.Reason);
        result.Rows.Select(static x => x.Cells[1].Text).Should().Equal("12", "10");
        result.Columns.Should().NotContain("Tenant", "the rows are one tenant's, already named in the header");
        result.Summary.Should().Contain("in this database of tenant acme");
    }

    [PostgresFact]
    public async Task On_a_conjoined_store_with_no_tenant_a_visitor_without_ViewDatabaseActivity_is_refused_and_nothing_is_read()
    {
        await using LongestStreamsHost host = await LongestStreamsHost.CreateAsync(postgres, "lsa_notenant", conjoined: true);
        await host.StartAsync<HealthCounter>(12, "acme");

        HealthActionResult result = await host.PressAsync();

        result.Outcome.Should().Be(HealthActionOutcome.Refused);
        result.Reason.Should().Contain("each stream belongs to one tenant").And.Contain("Pick a tenant in the header");
        result.Rows.Should().BeEmpty();
        result.Statements.Should().BeEmpty("no statement was built");
        host.Audit().Should().ContainSingle().Which.Capability.Should().Be(nameof(StudioCapability.ViewDatabaseActivity));
    }

    [PostgresFact]
    public async Task With_ViewDatabaseActivity_the_rows_name_their_tenants_and_a_tenant_the_store_policy_hides_is_left_out_and_counted()
    {
        await using LongestStreamsHost host = await LongestStreamsHost.CreateAsync(postgres, "lsa_activity", conjoined: true, activity: true);

        await host.StartAsync<HealthCounter>(12, "acme");
        await host.StartAsync<HealthCounter>(10, "acme");
        await host.StartAsync<HealthCounter>(30, "globex");
        await host.StartAsync<HealthCounter>(20, "hidden");

        host.Policy.AllowEverything();
        HealthActionResult all = await host.PressAsync();

        all.Columns.Should().Contain("Tenant");
        all.Rows.Select(static x => (x.Cells[1].Text, x.Cells[4].Text)).Should().Equal(("30", "globex"), ("20", "hidden"), ("12", "acme"), ("10", "acme"));
        all.AuditCapability.Should().Be(StudioCapability.ViewDatabaseActivity);

        host.Policy.Allow(static resource => resource.TenantId != "hidden");
        HealthActionResult narrowed = await host.PressAsync();

        narrowed.Outcome.Should().Be(HealthActionOutcome.Done, narrowed.Reason);
        narrowed.Rows.Select(static x => x.Cells[4].Text).Should().Equal("globex", "acme", "acme");
        narrowed.Notes.Should().Contain("1 of the longest streams is of tenants your account may not see, and is not listed.");
        narrowed.Rows.SelectMany(static x => x.Cells).Select(static x => x.Text).Should().NotContain("hidden");
        narrowed.AuditMessage.Should().Be("Listed 3 of the longest live streams, 1 of tenants not shown.");

        host.Audit().Select(static x => x.Message).Should().Contain("Listed 3 of the longest live streams, 1 of tenants not shown.");
    }

    [PostgresFact]
    public async Task A_write_policy_that_refuses_ViewDatabaseActivity_refuses_the_cross_tenant_list()
    {
        await using LongestStreamsHost host = await LongestStreamsHost.CreateAsync(postgres, "lsa_writepolicy", conjoined: true, activity: true);
        await host.StartAsync<HealthCounter>(12, "acme");

        host.Policy.DenyWrites();
        HealthActionResult result = await host.PressAsync();

        result.Outcome.Should().Be(HealthActionOutcome.Refused);
        result.Reason.Should().Contain("write policy");
        result.Rows.Should().BeEmpty();
    }

    // ---------------------------------------------------------------------------------------------------
    // Hidden types
    // ---------------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task A_hidden_aggregates_streams_are_absent_and_its_name_is_nowhere_in_what_the_visitor_is_given()
    {
        await using LongestStreamsHost host = await LongestStreamsHost.CreateAsync(
            postgres, "lsa_hidden", studio: static options => options.IsDocumentTypeVisible = static type => type != typeof(LongSecret));

        await host.StartAsync<HealthCounter>(5);
        await host.StartAsync<LongSecret>(900);
        Guid ghost = await host.StartAsync<HealthCounter>(800);
        await using (NpgsqlConnection admin = await postgres.OpenAsync(Token))
        {
            await using NpgsqlCommand rename = new("update " + SqlIdentifier.Qualify("lsa_hidden", "mt_streams") + " set type = 'ghost_aggregate' where id = @id", admin);
            rename.Parameters.AddWithValue("id", ghost);
            await rename.ExecuteNonQueryAsync(Token);
        }

        HealthActionResult result = await host.PressAsync();

        result.Outcome.Should().Be(HealthActionOutcome.Done, result.Reason);
        // Only the visible aggregate's stream is read: the hidden type's, and one whose alias this process cannot map, are not.
        result.Rows.Select(static x => x.Cells[1].Text).Should().Equal("5");
        result.Notes.Should().Contain("Streams of aggregate types this application hides from the studio are left out.");

        string everything = string.Join('\n', result.Rows.SelectMany(static x => x.Cells).Select(static x => x.Text))
            + result.Summary + string.Join('\n', result.Notes) + string.Join('\n', result.Remedies.Select(static x => x.Code)) + result.AuditMessage;
        everything.Should().NotContainEquivalentOf("secret").And.NotContainEquivalentOf("ghost").And.NotContain("900").And.NotContain("800");
        result.Statements[0].Sql.Should().Contain("= any(@shown)");
    }

    // ---------------------------------------------------------------------------------------------------
    // Failures that are expected states
    // ---------------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task A_table_locked_for_a_schema_change_is_a_timeout_with_the_lock_sentence_and_not_a_logged_anomaly()
    {
        await using LongestStreamsHost host = await LongestStreamsHost.CreateAsync(postgres, "lsa_lock");
        await host.StartAsync<HealthCounter>(5);

        // Warm the column catalog: the read in front of the transaction is not what is being locked.
        (await host.PressAsync()).Outcome.Should().Be(HealthActionOutcome.Done);

        await using NpgsqlConnection holder = await postgres.OpenAsync(Token);
        await using NpgsqlTransaction migration = await holder.BeginTransactionAsync(Token);
        await using NpgsqlCommand lockIt = new("lock table " + SqlIdentifier.Qualify("lsa_lock", "mt_streams") + " in access exclusive mode", holder, migration);
        await lockIt.ExecuteNonQueryAsync(Token);

        HealthActionResult result = await host.PressAsync().WaitAsync(TimeSpan.FromSeconds(60), Token);

        result.Outcome.Should().Be(HealthActionOutcome.TimedOut);
        result.Reason.Should().Be("mt_streams is locked for a schema change right now, so the read would queue behind it; try again later.");
        host.Audit().First().Succeeded.Should().BeFalse("the ring is newest first");

        await migration.RollbackAsync(Token);
        (await host.PressAsync()).Outcome.Should().Be(HealthActionOutcome.Done, "the lock is gone, so is the timeout");
    }

    [PostgresFact]
    public async Task A_console_role_without_SELECT_on_mt_streams_gets_the_privilege_sentence()
    {
        await using (NpgsqlConnection admin = await postgres.OpenAsync(Token))
        {
            await using NpgsqlCommand create = new(
                "do $$ begin if not exists (select from pg_roles where rolname = 'lsa_noselect') then create role lsa_noselect nologin; end if; end $$",
                admin);
            await create.ExecuteNonQueryAsync(Token);
        }

        await using LongestStreamsHost host = await LongestStreamsHost.CreateAsync(
            postgres, "lsa_role", studio: static options => options.SqlConsoleRole = "lsa_noselect");
        await host.StartAsync<HealthCounter>(5);

        HealthActionResult result = await host.PressAsync();

        result.Outcome.Should().Be(HealthActionOutcome.Refused);
        result.Reason.Should().Be("The role the studio reads as may not select from mt_streams.");
    }

    [PostgresFact]
    public async Task A_store_with_no_event_tables_yet_has_no_streams_to_measure()
    {
        await using LongestStreamsHost host = await LongestStreamsHost.CreateAsync(postgres, "lsa_noevents", createTables: false);

        HealthActionResult result = await host.PressAsync();

        result.Outcome.Should().Be(HealthActionOutcome.Refused);
        result.Reason.Should().Be("The event store's tables do not exist in this database yet, so there are no streams to measure.");
    }

    // ---------------------------------------------------------------------------------------------------
    // What is left behind
    // ---------------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task The_audit_ring_holds_a_count_and_no_stream_id_and_nothing_is_written()
    {
        await using LongestStreamsHost host = await LongestStreamsHost.CreateAsync(postgres, "lsa_audit");
        Guid id = await host.StartAsync<HealthCounter>(7);
        long before = await LiveWait.ScalarAsync<long>(postgres.ConnectionString, "select count(*) from " + SqlIdentifier.Qualify("lsa_audit", "mt_events"));

        HealthActionResult result = await host.PressAsync();

        result.Rows.Single().Cells[0].Text.Should().Be(id.ToString());

        StudioActionLogEntry entry = host.Audit().Should().ContainSingle().Subject;
        entry.Should().Match<StudioActionLogEntry>(static x =>
            x.Succeeded && x.Action == "Find the longest streams" && x.Target == "the event store's streams (mt_streams)" && x.Capability == null);
        entry.Message.Should().Be("Listed 1 of the longest live streams.");
        entry.Message.Should().NotContain(id.ToString());
        entry.Target.Should().NotContain(id.ToString());

        (await LiveWait.ScalarAsync<long>(postgres.ConnectionString, "select count(*) from " + SqlIdentifier.Qualify("lsa_audit", "mt_events")))
            .Should().Be(before, "an action never writes");
    }

    /// <summary>A studio over a real store with a few streams: the host every test above presses E06's button on.</summary>
    private sealed class LongestStreamsHost : IAsyncDisposable
    {
        private readonly ServiceProvider provider;

        private LongestStreamsHost(ServiceProvider provider, FakeStoreAuthorizationService policy, string schema)
        {
            this.provider = provider;
            Policy = policy;
            Schema = schema;
        }

        public FakeStoreAuthorizationService Policy { get; }

        public string Schema { get; }

        public IDocumentStore Store => provider.GetRequiredService<IDocumentStore>();

        public static async Task<LongestStreamsHost> CreateAsync(
            PostgresFixture postgres,
            string schema,
            bool conjoined = false,
            bool activity = false,
            Action<MartenStudioOptions>? studio = null,
            bool createTables = true)
        {
            await postgres.CreateSchemaAsync(schema, Token);

            DocumentStore store = DocumentStore.For(options =>
            {
                options.Connection(postgres.ConnectionString);
                options.DatabaseSchemaName = schema;
                options.Events.DatabaseSchemaName = schema;
                options.AutoCreateSchemaObjects = AutoCreate.All;
                options.Events.AddEventType<HealthPing>();
                options.Projections.LiveStreamAggregation<HealthCounter>();
                options.Projections.Snapshot<LongKept>(SnapshotLifecycle.Inline);
                options.Projections.LiveStreamAggregation<LongSecret>();

                if (conjoined)
                {
                    options.Events.TenancyStyle = TenancyStyle.Conjoined;
                    options.Policies.AllDocumentsAreMultiTenanted();
                }
            });

            if (createTables)
            {
                await store.Storage.ApplyAllConfiguredChangesToDatabaseAsync();
            }

            FakeStoreAuthorizationService policy = new();
            ServiceCollection services = new();
            services.AddLogging();
            services.AddAuthorization();
            services.AddSingleton<AuthenticationStateProvider, Anonymous>();
            services.AddSingleton<IDocumentStore>(store);
            services.AddSingleton<IAuthorizationService>(policy);
            services.AddMartenStudio(options =>
            {
                options.StoreAuthorizationPolicy = "studio-store";
                options.WriteAuthorizationPolicy = "studio-write";
                options.DiscoverTenantIds = false;
                options.KnownTenantIds.Add("acme");
                options.KnownTenantIds.Add("globex");
                options.KnownTenantIds.Add("hidden");
                options.Capabilities.ViewDatabaseActivity = activity;
                studio?.Invoke(options);
            });

            return new LongestStreamsHost(services.BuildServiceProvider(), policy, schema);
        }

        /// <summary>A stream of <paramref name="events" /> pings started as <typeparamref name="T" />.</summary>
        public Task<Guid> StartAsync<T>(int events, string? tenant = null)
            where T : class => StartStreamAsync(events, tenant, typed: true, aggregate: typeof(T));

        /// <summary>A stream of <paramref name="events" /> pings with no aggregate type.</summary>
        public Task<Guid> StartAsync(int events, string? tenant = null) => StartStreamAsync(events, tenant, typed: false, aggregate: null);

        private async Task<Guid> StartStreamAsync(int events, string? tenant, bool typed, Type? aggregate)
        {
            Guid id = Guid.CreateVersion7();
            object[] pings = [.. Enumerable.Range(1, events).Select(static n => (object) new HealthPing(n))];

            await using IDocumentSession session = tenant is null ? Store.LightweightSession() : Store.LightweightSession(tenant);

            if (typed)
            {
                session.Events.StartStream(aggregate!, id, pings);
            }
            else
            {
                session.Events.StartStream(id, pings);
            }

            await session.SaveChangesAsync(Token);
            return id;
        }

        public async Task ArchiveAsync(Guid stream)
        {
            await using IDocumentSession session = Store.LightweightSession();
            session.Events.ArchiveStream(stream);
            await session.SaveChangesAsync(Token);
        }

        /// <summary>The button, pressed as a visitor of the circuit would, on a synthetic E06 finding.</summary>
        public async Task<HealthActionResult> PressAsync(string? tenant = null)
        {
            HealthFinding finding = new()
            {
                Code = "E06",
                Severity = HealthSeverity.Info,
                Title = "Some streams are long",
                WhyItMatters = "Because.",
                Area = HealthArea.Events,
            };

            HealthReportFinding shown = new(finding, [], HealthService.MuteKeyOf("default", Store.Storage.Database.Id.Identity, finding), "https://example.invalid/", true, null);

            await using AsyncServiceScope circuit = provider.CreateAsyncScope();
            StudioScope scope = new("default", Store.Storage.Database.Id.Identity, tenant);

            return await circuit.ServiceProvider.GetRequiredService<IHealthActionService>().RunAsync(scope, shown, Action, Token);
        }

        public StudioActionLogEntry[] Audit()
        {
            using IServiceScope circuit = provider.CreateScope();

            return [.. circuit.ServiceProvider.GetRequiredService<StudioActionLog>().GetLatest().Where(static x => x.Action == "Find the longest streams")];
        }

        public async ValueTask DisposeAsync()
        {
            await Store.DisposeAsync();
            await provider.DisposeAsync();
        }
    }

    private sealed class Anonymous : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity())));
    }
}
