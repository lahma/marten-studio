using Marten;

using PostgresStudio.Services.Database;
using PostgresStudio.Services.Sources;
using MartenStudio.Tests.Health;
using MartenStudio.Tests.Support;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Npgsql;
using PostgresStudio.Services;
using PostgresStudio.Services.Health;

namespace MartenStudio.Tests.Database;

/// <summary>
/// Database › Sessions' service (acceptance 3, the page's half; decision 4): the summary for anybody the store
/// policy passes for the database as a whole, the details only through <c>ViewDatabaseActivity</c> - every
/// refusal a value and an audit entry, the first read audited by signature and never per poll, and one snapshot
/// shared by both. A real studio container over a store that never connects, with the snapshot reader faked.
/// </summary>
public class SessionActivityServiceTests
{
    private static CancellationToken Token => Xunit.TestContext.Current.CancellationToken;

    private static readonly StudioScope Scope = new("default", string.Empty, null);

    // ----------------------------------------------------------------------------------------------
    // The summary
    // ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_summary_counts_and_ages_without_naming_anybody_and_says_what_the_details_need()
    {
        await using Harness harness = Harness.Create(static options => options.Capabilities = new MartenStudioCapabilities());

        SessionActivitySummary summary = await harness.Service.GetSummaryAsync(Scope, Token);

        summary.Refusal.Should().Be(DatabaseRefusal.None);
        summary.SessionCount.Should().Be(3);
        summary.ByState.Should().Contain(new KeyValuePair<string, int>("idle in transaction", 1));
        summary.Waiting.Should().Be(1);
        summary.LongestTransaction.Should().Be(TimeSpan.FromMinutes(12));
        summary.PreparedCount.Should().Be(1);
        summary.Details.Refusal.Should().Be(DatabaseRefusal.CapabilityOff);
        summary.Details.Denial.Should().Contain("MartenStudioOptions.Capabilities.ViewDatabaseActivity");

        summary.Locks.Select(static x => x.Target).Should().Contain("a relation Marten does not own")
            .And.NotContain(static x => x.Contains("employees", StringComparison.Ordinal) || x.Contains("legacy", StringComparison.Ordinal),
                "the name of a relation Marten does not own is a detail");

        harness.Reader.Reads.Should().Be(1);
        harness.Ring.Should().BeEmpty("the summary is a plain read, and the page's question about the gate is never audited");
    }

    /// <summary>
    /// Issue #8 G9: the database's sessions by state and the server's budget are database-wide, so the summary carries them;
    /// the applications are named only in the details, behind ViewDatabaseActivity - the summary says how many, and no name.
    /// </summary>
    [Fact]
    public async Task Connections_by_application_are_totals_in_the_summary_and_names_only_in_the_details()
    {
        await using Harness harness = Harness.Create(static options => options.Capabilities = MartenStudioCapabilities.All());

        SessionActivitySummary summary = await harness.Service.GetSummaryAsync(Scope, Token);

        summary.Connections.Should().Be(new SessionApplicationCounts(null, 3, 1, 1, 1, 1, 0, TimeSpan.FromMinutes(12)));
        summary.Budget.Should().Be(new SessionConnectionBudget(100, 3, 9, true));
        summary.ApplicationCount.Should().Be(2, "a count, which names no application");
        System.Text.Json.JsonSerializer.Serialize(summary)
            .Should().NotContain("billing-api", "an application's name is a detail").And.NotContain("psql");

        SessionActivityDetails details = await harness.Service.GetDetailsAsync(Scope, Token);

        details.Applications.Select(static x => (x.Application, x.Total)).Should().Equal(("billing-api", 2L), ("psql", 1L));
        details.ApplicationCount.Should().Be(2);
        harness.Reader.Reads.Should().Be(1, "one snapshot serves both halves");
    }

    [Fact]
    public async Task The_summary_needs_the_store_policy_for_the_database_as_a_whole()
    {
        await using Harness harness = Harness.Create(static options => options.Capabilities = MartenStudioCapabilities.All(), policies: true);

        // A visitor confined to a tenant: allowed with one, refused without.
        harness.Policies.Allow(static resource => resource.TenantId is not null);

        SessionActivitySummary summary = await harness.Service.GetSummaryAsync(Scope with { TenantId = "acme" }, Token);

        summary.Refusal.Should().Be(DatabaseRefusal.StorePolicy, "a session belongs to no tenant");
        harness.Reader.Reads.Should().Be(0, "nothing about the database is read before the policy said yes");
    }

    [Fact]
    public async Task A_failed_read_is_a_value_in_the_studios_words_and_never_Postgres_own()
    {
        await using Harness harness = Harness.Create(static options => options.Capabilities = MartenStudioCapabilities.All());
        harness.Reader.Throws = new NpgsqlException("relation \"hr.secret\" does not exist (pretend)");

        SessionActivitySummary summary = await harness.Service.GetSummaryAsync(Scope, Token);

        summary.Refusal.Should().Be(DatabaseRefusal.Unavailable);
        summary.Reason.Should().NotContain("hr.secret");
        summary.Details.IsOpen.Should().BeTrue("the gate's answer does not depend on the read");
    }

    // ----------------------------------------------------------------------------------------------
    // The details: three refusals, each a value and an audit entry, before anything is read
    // ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task With_the_capability_off_the_details_are_refused_and_audited_and_nothing_is_read()
    {
        await using Harness harness = Harness.Create(static options => options.Capabilities = new MartenStudioCapabilities(), policies: true);

        SessionActivityDetails details = await harness.Service.GetDetailsAsync(Scope, Token);

        details.Refusal.Should().Be(DatabaseRefusal.CapabilityOff);
        details.Reason.Should().Contain("MartenStudioOptions.Capabilities.ViewDatabaseActivity");
        details.Sessions.Should().BeEmpty();

        StudioActionLogEntry entry = harness.Ring.Should().ContainSingle().Subject;
        entry.Succeeded.Should().BeFalse();
        entry.Capability.Should().Be(nameof(StudioCapability.ViewDatabaseActivity));
        harness.Policies.Calls.Should().BeEmpty("a capability that is off is answered before anybody is asked who is asking");
        harness.Reader.Reads.Should().Be(0);
    }

    /// <summary>
    /// W5-API-fix F1: the Health page's Snapshot download, built by a visitor short of <c>ViewDatabaseActivity</c>, asks the
    /// details nothing - the summary already says the gate is shut - so a click on the menu records no refusal: no ring entry
    /// and no 9202 (D27: using a page whose gate is closed refuses nothing). The same visitor asking the details directly is
    /// refused and audited, which is what makes this a test of the builder rather than of a quiet service.
    /// </summary>
    [Fact]
    public async Task The_snapshot_download_asks_no_details_of_a_visitor_the_summary_shuts_out()
    {
        CapturingLoggerProvider logs = new();
        await using Harness harness = Harness.Create(static options => options.Capabilities = new MartenStudioCapabilities(), policies: true, logs: logs);
        await using AsyncServiceScope circuit = harness.Provider.CreateAsyncScope();

        PostgresStudio.Services.Diagnostics.VisitorSnapshot snapshot = await circuit.ServiceProvider
            .GetRequiredService<PostgresStudio.Services.Diagnostics.VisitorSnapshotBuilder>()
            .BuildAsync(Scope, Token);

        harness.Ring.Should().BeEmpty("the menu's click refuses nothing");
        logs.Entries.Should().NotContain(static x => x.EventId.Id == 9202 || x.EventId.Id == 9203);

        using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(snapshot.Json);
        document.RootElement.GetProperty("withheld").GetProperty("connections.byApplication").GetString()
            .Should().Contain("MartenStudioOptions.Capabilities.ViewDatabaseActivity", "the summary's own sentence says what the names need");
        document.RootElement.GetProperty("connections").GetProperty("database").GetProperty("total").GetInt32().Should().Be(3);

        // The anti-vacuity half: the details asked for directly are refused, audited and logged.
        await circuit.ServiceProvider.GetRequiredService<ISessionActivityService>().GetDetailsAsync(Scope, Token);
        harness.Ring.Should().ContainSingle();
        logs.Entries.Should().Contain(static x => x.EventId.Id == 9202);
    }

    [Fact]
    public async Task ReadOnly_refuses_the_details_and_audits_it()
    {
        await using Harness harness = Harness.Create(static options =>
        {
            options.Capabilities = MartenStudioCapabilities.All();
            options.ReadOnly = true;
        });

        SessionActivityDetails details = await harness.Service.GetDetailsAsync(Scope, Token);

        details.Refusal.Should().Be(DatabaseRefusal.ReadOnly);
        harness.Ring.Should().ContainSingle().Which.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task A_write_policy_that_refuses_is_asked_with_no_tenant_and_audited()
    {
        await using Harness harness = Harness.Create(static options => options.Capabilities = MartenStudioCapabilities.All(), policies: true);
        harness.Policies.Allow(static resource => resource.Capability is null);

        SessionActivityDetails details = await harness.Service.GetDetailsAsync(Scope with { TenantId = "acme" }, Token);

        details.Refusal.Should().Be(DatabaseRefusal.WritePolicy);
        harness.Policies.Calls.Should().NotBeEmpty().And.OnlyContain(static x => x.Resource.TenantId == null,
            "a session belongs to no tenant, so the database as a whole is what is asked about");
        harness.Policies.Calls.Should().Contain(static x => x.Resource.Capability == nameof(StudioCapability.ViewDatabaseActivity));
        harness.Ring.Should().ContainSingle().Which.Capability.Should().Be(nameof(StudioCapability.ViewDatabaseActivity));
        harness.Reader.Reads.Should().Be(0);
    }

    [Fact]
    public async Task A_store_policy_that_refuses_the_database_as_a_whole_refuses_the_details()
    {
        await using Harness harness = Harness.Create(static options => options.Capabilities = MartenStudioCapabilities.All(), policies: true);
        harness.Policies.Allow(static resource => resource.Capability is not null);

        SessionActivityDetails details = await harness.Service.GetDetailsAsync(Scope, Token);

        details.Refusal.Should().Be(DatabaseRefusal.StorePolicy);
        harness.Ring.Should().ContainSingle().Which.Succeeded.Should().BeFalse();
    }

    // ----------------------------------------------------------------------------------------------
    // The details: through the gate
    // ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Through_the_gate_the_details_name_the_sessions_and_withhold_every_statement_when_the_schemas_cannot_be_checked()
    {
        await using Harness harness = Harness.Create(static options => options.Capabilities = MartenStudioCapabilities.All());

        SessionActivityDetails details = await harness.Service.GetDetailsAsync(Scope, Token);

        details.Refusal.Should().Be(DatabaseRefusal.None);
        details.Sessions.Select(static x => x.ProcessId).Should().Contain(4242);
        details.Sessions.Single(static x => x.ProcessId == 4242).User.Should().Be("app_user");
        details.Prepared.Should().ContainSingle().Which.Gid.Should().Be("gid-orphan");

        // This store never connects, so the visitor's withheld schemas cannot be read: fail closed.
        details.StatementsWithheld.Should().BeTrue();
        details.Sessions.Should().OnlyContain(static x => x.Statement.Text == null);
        details.Locks.Should().OnlyContain(static x => x.Relation == null, "a relation in a schema that may be withheld is not named");
    }

    [Fact]
    public async Task The_first_read_of_the_details_is_audited_once_and_a_refresh_is_not()
    {
        await using Harness harness = Harness.Create(static options => options.Capabilities = MartenStudioCapabilities.All());

        await harness.Service.GetDetailsAsync(Scope, Token);
        await harness.Service.GetDetailsAsync(Scope, Token);
        await harness.Service.GetDetailsAsync(Scope, Token);

        StudioActionLogEntry entry = harness.Ring.Should().ContainSingle("the page's interval reads and Refresh repeat the signature").Subject;
        entry.Succeeded.Should().BeTrue();
        entry.Action.Should().Be(SessionActivityService.DetailsAction);
        entry.Message.Should().Contain("3 sessions").And.NotContain("4242", "the ring is readable by more people than the page");
        entry.Capability.Should().Be(nameof(StudioCapability.ViewDatabaseActivity));

        // A circuit of its own is a page view of its own.
        await using AsyncServiceScope other = harness.Provider.CreateAsyncScope();
        await other.ServiceProvider.GetRequiredService<ISessionActivityService>().GetDetailsAsync(Scope, Token);

        harness.Ring.Where(static x => x.Succeeded).Should().HaveCount(2);
    }

    /// <summary>
    /// B1: the summary - which anybody the store policy passes reads - labels locks with the classifier over
    /// <b>every</b> registered store, so another store's table is Marten's and named to nobody, and a lock on part
    /// of this store's own table is labelled by the table.
    /// </summary>
    [Fact]
    public async Task The_summarys_lock_labels_come_from_every_stores_declarations_and_name_only_this_stores_own()
    {
        await using Harness harness = Harness.Create(static options => options.Capabilities = new MartenStudioCapabilities(), otherStore: true);

        harness.Reader.Snapshot = harness.Reader.Snapshot with
        {
            Locks =
            [
                new LockRow(4242, "relation", "RowExclusiveLock", true, new LockedRelation(OtherSessionsStore.Schema, "mt_doc_otherledger", "r"), null, null),
                new LockRow(4242, "relation", "RowExclusiveLock", true, new LockedRelation("sessions_unit", "mt_doc_sessionnote", "r", LockedPart.Index), null, null),
            ],
            TotalLocks = 2,
        };

        SessionActivitySummary summary = await harness.Service.GetSummaryAsync(Scope, Token);

        summary.Locks.Select(static x => x.Target).Should().BeEquivalentTo(
            "a Marten relation this store does not declare",
            "an index of the sessionnote document table (sessions_unit.mt_doc_sessionnote)");
        summary.Locks.Should().NotContain(static x => x.Target.Contains("otherledger", StringComparison.Ordinal),
            "another store's alias is named to nobody");
    }

    // ----------------------------------------------------------------------------------------------
    // SEC2 L2: the tenant-less questions name the database the scope resolved to, not the visitor's spelling
    // ----------------------------------------------------------------------------------------------

    /// <summary>
    /// A link with no <c>?db=</c> means the store's first database. The resolver asks about that spelling - it must ask
    /// before it looks anything up - and every question after it, and the audit, names the database's own identity, as
    /// the Health page and the high-water Explain do. A policy keyed on the identity grants the details here, and was
    /// asked about nothing else.
    /// </summary>
    [Fact]
    public async Task The_gate_questions_and_the_audit_name_the_resolved_database_when_the_scope_spells_it_empty()
    {
        await using Harness harness = Harness.Create(static options => options.Capabilities = MartenStudioCapabilities.All(), policies: true);
        string identity = harness.Identity;
        harness.Policies.Allow(resource => resource.Capability is null || resource.DatabaseIdentifier == identity);

        SessionActivitySummary summary = await harness.Service.GetSummaryAsync(Scope, Token);
        SessionActivityDetails details = await harness.Service.GetDetailsAsync(Scope, Token);

        summary.Details.IsOpen.Should().BeTrue("the page's question about the details is asked about the database the scope resolved to");
        details.Refusal.Should().Be(DatabaseRefusal.None);

        harness.Policies.Calls.Where(static x => x.Resource.Capability is not null).Should().NotBeEmpty()
            .And.OnlyContain(x => x.Resource.DatabaseIdentifier == identity && x.Resource.TenantId == null);
        harness.Policies.Calls.Where(x => x.Resource.Capability is null && x.Resource.DatabaseIdentifier == identity)
            .Should().NotBeEmpty("the store policy is asked about the identity too, beside the resolver's own question");

        harness.Ring.Should().ContainSingle().Which.DatabaseId.Should().Be(identity, "the audit names the resource the policy was shown");
    }

    /// <summary>The refusing half: a policy that grants only the empty spelling is not asked about it, and refuses.</summary>
    [Fact]
    public async Task A_policy_that_grants_only_the_visitors_spelling_refuses_the_details_and_the_refusal_names_the_identity()
    {
        await using Harness harness = Harness.Create(static options => options.Capabilities = MartenStudioCapabilities.All(), policies: true);
        string identity = harness.Identity;
        harness.Policies.Allow(static resource => resource.Capability is null || resource.DatabaseIdentifier.Length == 0);

        SessionActivityDetails details = await harness.Service.GetDetailsAsync(Scope, Token);

        details.Refusal.Should().Be(DatabaseRefusal.WritePolicy);
        harness.Reader.Reads.Should().Be(0);

        StudioActionLogEntry entry = harness.Ring.Should().ContainSingle().Subject;
        entry.Succeeded.Should().BeFalse();
        entry.DatabaseId.Should().Be(identity);
    }

    /// <summary>
    /// SEC2-fix item 1: <c>?store=DEFAULT</c> reaches the store registered as <c>default</c>, and the questions after the
    /// resolver's, and the audit, name the registered key - so a policy that compares keys ordinally grants the details.
    /// </summary>
    [Fact]
    public async Task A_store_key_spelt_in_another_case_is_asked_and_audited_under_the_registered_key()
    {
        await using Harness harness = Harness.Create(static options => options.Capabilities = MartenStudioCapabilities.All(), policies: true);
        harness.Policies.Allow(static resource =>
            resource.Capability is null || string.Equals(resource.StoreName, "default", StringComparison.Ordinal));

        StudioScope shouted = Scope with { StoreKey = "DEFAULT" };

        SessionActivitySummary summary = await harness.Service.GetSummaryAsync(shouted, Token);
        SessionActivityDetails details = await harness.Service.GetDetailsAsync(shouted, Token);

        summary.Details.IsOpen.Should().BeTrue();
        details.Refusal.Should().Be(DatabaseRefusal.None);

        harness.Policies.Calls.Where(static x => x.Resource.Capability is not null).Should().NotBeEmpty()
            .And.OnlyContain(static x => x.Resource.StoreName == "default");
        harness.Ring.Should().ContainSingle().Which.StoreKey.Should().Be("default");
    }

    [Fact]
    public async Task The_summary_and_the_details_share_one_read()
    {
        await using Harness harness = Harness.Create(static options => options.Capabilities = MartenStudioCapabilities.All());

        await harness.Service.GetSummaryAsync(Scope, Token);
        await harness.Service.GetDetailsAsync(Scope, Token);
        await harness.Service.GetSummaryAsync(Scope, Token);

        harness.Reader.Reads.Should().Be(1, "pg_stat_activity and pg_locks are read once per database every ten seconds at most");
    }

    // ----------------------------------------------------------------------------------------------
    // The snapshot floor
    // ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_snapshot_is_read_again_only_once_it_is_ten_seconds_old()
    {
        FakeSessionActivityReader reader = new();
        FakeTimeProvider clock = new(new DateTimeOffset(2026, 10, 15, 9, 0, 0, TimeSpan.Zero));
        SessionActivitySnapshots snapshots = new(reader, clock);

        await using HealthHarness health = HealthHarness.Create();

        await snapshots.GetAsync(health.Resolved, Token);
        clock.Advance(TimeSpan.FromSeconds(9));
        await snapshots.GetAsync(health.Resolved, Token);

        reader.Reads.Should().Be(1);

        clock.Advance(TimeSpan.FromSeconds(1));
        await snapshots.GetAsync(health.Resolved, Token);

        reader.Reads.Should().Be(2);
        snapshots.ReadCount.Should().Be(2);
        SessionActivitySnapshots.MinimumAge.Should().Be(TimeSpan.FromSeconds(10));
    }

    // ----------------------------------------------------------------------------------------------
    // The harness
    // ----------------------------------------------------------------------------------------------

    private sealed class Harness : IAsyncDisposable
    {
        private readonly AsyncServiceScope circuit;

        private Harness(ServiceProvider provider, TestStoreAuthorizationService policies, FakeSessionActivityReader reader)
        {
            Provider = provider;
            Policies = policies;
            Reader = reader;
            circuit = provider.CreateAsyncScope();
        }

        public ServiceProvider Provider { get; }

        public TestStoreAuthorizationService Policies { get; }

        public FakeSessionActivityReader Reader { get; }

        public ISessionActivityService Service => circuit.ServiceProvider.GetRequiredService<ISessionActivityService>();

        public IReadOnlyList<StudioActionLogEntry> Ring => Provider.GetRequiredService<StudioActionLogService>().GetLatest();

        /// <summary>The store's one database's own identity, which an empty <c>?db=</c> resolves to.</summary>
        public string Identity => Provider.GetRequiredService<IDocumentStore>().Storage.Database.Id.Identity;

        public static Harness Create(
            Action<MartenStudioOptions> configure,
            bool policies = false,
            bool otherStore = false,
            CapturingLoggerProvider? logs = null)
        {
            TestAuthenticationStateProvider users = new();
            users.SignIn("operator");

            TestStoreAuthorizationService authorization = new();
            FakeSessionActivityReader reader = new();

            ServiceCollection services = new();
            services.AddLogging(builder =>
            {
                if (logs is not null)
                {
                    builder.SetMinimumLevel(LogLevel.Trace);
                    builder.AddProvider(logs);
                }
            });
            services.AddMarten(static options =>
            {
                options.Connection(HealthHarness.RefusedConnectionString);
                options.DatabaseSchemaName = "sessions_unit";
                options.Schema.For<SessionNote>();
            });

            if (otherStore)
            {
                services.AddMartenStore<OtherSessionsStore.IStore>(static options =>
                {
                    options.Connection(HealthHarness.RefusedConnectionString);
                    options.DatabaseSchemaName = OtherSessionsStore.Schema;
                    options.Schema.For<OtherLedger>();
                });
            }

            services.AddMartenStudio(options =>
            {
                if (policies)
                {
                    options.StoreAuthorizationPolicy = "store-policy";
                    options.WriteAuthorizationPolicy = "write-policy";
                }

                configure(options);
            });

            services.AddSingleton<IAuthorizationService>(authorization);
            services.AddScoped<AuthenticationStateProvider>(_ => users);
            services.AddSingleton<ISessionActivityReader>(reader);

            return new Harness(services.BuildServiceProvider(), authorization, reader);
        }

        public async ValueTask DisposeAsync()
        {
            await circuit.DisposeAsync();
            await Provider.DisposeAsync();
        }
    }
}

/// <summary>A document type of the harness's own store.</summary>
public sealed class SessionNote
{
    /// <summary>Its id.</summary>
    public Guid Id { get; set; }
}

/// <summary>A document type of the other store.</summary>
public sealed class OtherLedger
{
    /// <summary>Its id.</summary>
    public Guid Id { get; set; }
}

/// <summary>A second registered store, in a schema of its own.</summary>
public static class OtherSessionsStore
{
    /// <summary>Its schema.</summary>
    public const string Schema = "sessions_other";

    /// <summary>Its marker interface.</summary>
    public interface IStore : IDocumentStore;
}

/// <summary>A snapshot reader that hands back what a test says, and counts.</summary>
internal sealed class FakeSessionActivityReader : ISessionActivityReader
{
    private static readonly DateTimeOffset ReadAt = new(2026, 10, 15, 9, 0, 0, TimeSpan.Zero);

    private int reads;

    /// <summary>How many times it was asked.</summary>
    public int Reads => Volatile.Read(ref reads);

    /// <summary>What it throws instead of answering.</summary>
    public Exception? Throws { get; set; }

    /// <summary>What it answers: three sessions, two locks and a prepared transaction.</summary>
    public SessionSnapshot Snapshot { get; set; } = new(
        ReadAt,
        SessionVisibility.Everything,
        3,
        [
            new SessionRow(4242, "app_user", "billing-api", "10.1.2.3", ReadAt.AddHours(-1), ReadAt.AddMinutes(-12), ReadAt.AddMinutes(-11),
                "idle in transaction", "Client", "ClientRead", true, false, TimeSpan.FromMinutes(12), TimeSpan.FromMinutes(11), TimeSpan.FromMinutes(11),
                [], "update legacy.employees set salary = 1 where id = 7", false),
            new SessionRow(4343, "app_user", "billing-api", "10.1.2.4", ReadAt.AddHours(-1), ReadAt.AddSeconds(-40), ReadAt.AddSeconds(-40),
                "active", "Lock", "transactionid", false, false, TimeSpan.FromSeconds(40), TimeSpan.FromSeconds(40), TimeSpan.FromSeconds(40),
                [4242], "update legacy.employees set salary = 2 where id = 7", false),
            new SessionRow(4444, "reporter", "psql", null, ReadAt.AddHours(-2), null, ReadAt.AddMinutes(-3),
                "idle", null, null, false, false, null, TimeSpan.FromMinutes(3), TimeSpan.FromMinutes(3), [], "select 1", false),
        ],
        2,
        [
            new LockRow(4343, "transactionid", "ShareLock", false, null, null, TimeSpan.FromSeconds(40)),
            new LockRow(4242, "relation", "RowExclusiveLock", true, new LockedRelation("legacy", "employees", "r"), null, null),
        ],
        10,
        [new PreparedRow("gid-orphan", "tx_owner", ReadAt.AddMinutes(-30), TimeSpan.FromMinutes(30), "901")],
        [new HealthStatement("select 1", [])])
    {
        // Issue #8 G9: the same three sessions, by application.
        Connections = new SessionConnections(
            true,
            100,
            3,
            9,
            new SessionApplicationCounts(null, 3, 1, 1, 1, 1, 0, TimeSpan.FromMinutes(12)),
            [
                new SessionApplicationCounts("billing-api", 2, 1, 0, 1, 1, 0, TimeSpan.FromMinutes(12)),
                new SessionApplicationCounts("psql", 1, 0, 1, 0, 0, 0, null),
            ],
            2),
    };

    /// <inheritdoc />
    public Task<SessionSnapshot> ReadAsync(ResolvedSource resolved, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref reads);

        return Throws is { } exception ? Task.FromException<SessionSnapshot>(exception) : Task.FromResult(Snapshot);
    }
}
