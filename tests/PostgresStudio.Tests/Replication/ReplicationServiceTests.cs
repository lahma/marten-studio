using MartenStudio.Tests.Support;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Npgsql;

using PostgresStudio.Services;
using PostgresStudio.Services.Health.Checks;
using PostgresStudio.Services.Replication;
using PostgresStudio.Services.Sources;
using PostgresStudio.Tests.Support;

using static PostgresStudio.Tests.Replication.ReplicationTestFacts;

namespace PostgresStudio.Tests.Replication;

/// <summary>
/// <see cref="ReplicationService" />'s gates and audit (W5-R2-fix 5), over a real Postgres Studio container whose data source
/// never connects, with the facts reader faked: the store policy for the database as a whole, the activity gate in its
/// order (capability, ReadOnly, write policy), the second question when the visitor's spelling is not the resolved
/// identity, the audit once per circuit and never for a view without names, and the tablespace size one at a time.
/// </summary>
public class ReplicationServiceTests
{
    private const string Refused = "Host=127.0.0.1;Port=1;Database=app;Username=app;Password=secret;Timeout=1";

    private static readonly StudioScope Scope = new("default", "default", null);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Through_every_gate_names_are_shown_and_the_first_view_is_audited_once_per_circuit()
    {
        await using Harness harness = Harness.Create(static options => options.Capabilities = PostgresStudioCapabilities.All(), policies: true);

        ReplicationView first = await harness.Service.GetAsync(Scope, Token);
        ReplicationView second = await harness.Service.GetAsync(Scope, Token);

        first.NamesShown.Should().BeTrue();
        first.Standbys.Value!.Rows.Single().Name.Should().Be("reporting");
        second.NamesShown.Should().BeTrue();

        StudioActionLogEntry entry = harness.Ring.Should().ContainSingle("a second view in the same circuit records nothing").Subject;
        entry.Action.Should().Be(ReplicationService.NamesAction);
        entry.Succeeded.Should().BeTrue();
        entry.Capability.Should().Be(nameof(StudioCapability.ViewDatabaseActivity));

        await using (AsyncServiceScope other = harness.Provider.CreateAsyncScope())
        {
            await other.ServiceProvider.GetRequiredService<IReplicationService>().GetAsync(Scope, Token);
        }

        harness.Ring.Should().HaveCount(2, "another circuit is another first view");
        harness.Reader.Reads.Should().Be(1, "the facts are the database's, read once for every circuit within an interval");
        harness.Policies.Calls.Should().Contain(x => x.Resource.Capability == nameof(StudioCapability.ViewDatabaseActivity));
    }

    [Fact]
    public async Task A_view_without_names_is_never_audited()
    {
        // Nothing to name: no standby, no slot of this database, no archive command text.
        await using Harness harness = Harness.Create(static options => options.Capabilities = PostgresStudioCapabilities.All());
        harness.Reader.Facts = Nameless();

        ReplicationView view = await harness.Service.GetAsync(Scope, Token);

        view.NamesShown.Should().BeTrue("the gate is open");
        harness.Ring.Should().BeEmpty("nothing the gate guards was shown");
    }

    [Fact]
    public async Task With_the_capability_off_names_are_withheld_and_naming_the_option_and_nothing_is_audited()
    {
        await using Harness harness = Harness.Create(static options => options.Capabilities = new PostgresStudioCapabilities(), policies: true);

        ReplicationView view = await harness.Service.GetAsync(Scope, Token);

        view.Refusal.Should().Be(ReplicationRefusal.None, "the numbers are the database-wide gate's");
        view.NamesShown.Should().BeFalse();
        view.NamesWithheld.Should().Contain("PostgresStudioOptions.Capabilities.ViewDatabaseActivity");
        view.Standbys.Value!.Rows.Single().Name.Should().BeNull();
        harness.Policies.Calls.Should().NotContain(static x => x.Resource.Capability != null, "a capability that is off is answered before the write policy is asked");
        harness.Ring.Should().BeEmpty();
    }

    [Fact]
    public async Task ReadOnly_withholds_names_and_says_why()
    {
        await using Harness harness = Harness.Create(static options =>
        {
            options.Capabilities = PostgresStudioCapabilities.All();
            options.ReadOnly = true;
        });

        ReplicationView view = await harness.Service.GetAsync(Scope, Token);

        view.NamesShown.Should().BeFalse();
        view.NamesWithheld.Should().Contain("PostgresStudioOptions.ReadOnly is true");
        harness.Ring.Should().BeEmpty();
    }

    [Fact]
    public async Task A_write_policy_that_refuses_ViewDatabaseActivity_withholds_names_and_shows_the_numbers()
    {
        await using Harness harness = Harness.Create(static options => options.Capabilities = PostgresStudioCapabilities.All(), policies: true);
        harness.Policies.Allow(static resource => resource.Capability is null);

        ReplicationView view = await harness.Service.GetAsync(Scope, Token);

        view.Refusal.Should().Be(ReplicationRefusal.None);
        view.NamesShown.Should().BeFalse();
        view.NamesWithheld.Should().Contain("write policy").And.Contain("ViewDatabaseActivity");
        view.Standbys.Value!.Rows.Single().Row.BehindBytes.Should().Be(2048);
        view.Standbys.Value.Rows.Single().Name.Should().BeNull();
        harness.Ring.Should().BeEmpty();
    }

    [Fact]
    public async Task A_store_policy_that_refuses_the_database_refuses_everything_and_reads_nothing()
    {
        await using Harness harness = Harness.Create(static options => options.Capabilities = PostgresStudioCapabilities.All(), policies: true);
        harness.Policies.Allow(static _ => false);

        ReplicationView view = await harness.Service.GetAsync(Scope, Token);
        TablespaceSize size = await harness.Service.MeasureTablespaceAsync(Scope, "pg_default", Token);

        view.Refusal.Should().Be(ReplicationRefusal.StorePolicy);
        view.Reason.Should().Contain("PostgresStudioOptions.SourceAuthorizationPolicy");
        size.Bytes.Should().BeNull();
        size.Problem.Should().Be(view.Reason);
        harness.Reader.Reads.Should().Be(0);
        harness.Reader.Measured.Should().BeEmpty();
    }

    [Fact]
    public async Task A_visitors_spelling_that_is_not_the_resolved_identity_is_asked_again_by_the_resolved_names()
    {
        await using Harness harness = Harness.Create(static options => options.Capabilities = PostgresStudioCapabilities.All(), policies: true);
        string identity = await harness.IdentityAsync();

        // The resolver's own question takes the visitor's spelling (a database matches in any case); the store policy
        // refuses the resolved identity, which only the second question names.
        harness.Policies.Allow(resource => resource.DatabaseIdentifier != identity);

        ReplicationView view = await harness.Service.GetAsync(Scope with { DatabaseId = identity.ToUpperInvariant() }, Token);

        identity.Should().NotBe(identity.ToUpperInvariant(), "the spelling must differ for the second question to matter");
        view.Refusal.Should().Be(ReplicationRefusal.StorePolicy, "the database the other spelling resolved to is asked about by its own name");
        harness.Policies.Calls.Should().Contain(x => x.Resource.DatabaseIdentifier == identity && x.Resource.Capability == null);
        harness.Reader.Reads.Should().Be(0);
    }

    [Fact]
    public async Task One_tablespace_is_measured_at_a_time_per_database_for_the_whole_process()
    {
        await using Harness harness = Harness.Create(static options => options.Capabilities = PostgresStudioCapabilities.All());
        TaskCompletionSource<long?> hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Reader.Size = hold.Task;

        Task<TablespaceSize> first = harness.Service.MeasureTablespaceAsync(Scope, "pg_default", Token);

        await using AsyncServiceScope other = harness.Provider.CreateAsyncScope();
        TablespaceSize second = await other.ServiceProvider.GetRequiredService<IReplicationService>().MeasureTablespaceAsync(Scope, "pg_global", Token);

        second.Problem.Should().Be("Another tablespace of this database is being measured; one at a time.", "the guard is the process's, not the circuit's");

        hold.SetResult(42L * 1024 * 1024);
        (await first).Bytes.Should().Be(42L * 1024 * 1024);

        (await other.ServiceProvider.GetRequiredService<IReplicationService>().MeasureTablespaceAsync(Scope, "pg_global", Token)).Bytes
            .Should().Be(42L * 1024 * 1024, "the guard is released once the first measurement ends");
        harness.Reader.Measured.Should().Equal("pg_default", "pg_global");
    }

    [Fact]
    public async Task A_tablespace_the_role_may_not_size_is_not_readable_by_this_role()
    {
        await using Harness harness = Harness.Create(static options => options.Capabilities = PostgresStudioCapabilities.All());
        harness.Reader.Size = Task.FromException<long?>(new PostgresException("permission denied for tablespace pg_global", "ERROR", "ERROR", PostgresErrorCodes.InsufficientPrivilege));

        TablespaceSize size = await harness.Service.MeasureTablespaceAsync(Scope, "pg_global", Token);

        size.Bytes.Should().BeNull();
        size.Problem.Should().StartWith("Not readable by this role: pg_tablespace_size needs CREATE on the tablespace or pg_read_all_stats");
        harness.Logs.Should().BeEmpty("a privilege is the page's sentence, not a log line above Debug");
    }

    /// <summary>Facts with nothing a name could be drawn from.</summary>
    private static ReplicationFacts Nameless() =>
        ReplicationViewTests.Facts(standbys: []) with
        {
            Slots = ReplicationRead<ReplicationSlotSnapshot>.Of(new ReplicationSlotSnapshot(Now, 170011, false, -1, 0, 1,
                [ReplicationViewTests.Slot("billing_cdc", "logical", false, "wal2json", "billing")])),
            Archiver = ReplicationRead<ArchiverFacts>.Of(Archiver(settings: Archiving(command: string.Empty))),
        };

    private sealed class FakeFactsReader : IReplicationFactsReader
    {
        private int reads;

        public int Reads => Volatile.Read(ref reads);

        public ReplicationFacts Facts { get; set; } = ReplicationViewTests.Facts(standbys: [ReplicationViewTests.Standby()]);

        public Task<long?> Size { get; set; } = Task.FromResult<long?>(1L << 20);

        public List<string> Measured { get; } = [];

        public Task<ReplicationFacts> ReadAsync(ResolvedSource resolved, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref reads);
            return Task.FromResult(Facts);
        }

        public Task<long?> TablespaceSizeAsync(ResolvedSource resolved, string tablespace, CancellationToken cancellationToken)
        {
            lock (Measured)
            {
                Measured.Add(tablespace);
            }

            return Size;
        }
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly AsyncServiceScope circuit;
        private readonly CapturingLoggerProvider logs;

        private Harness(ServiceProvider provider, TestSourceAuthorizationService policies, FakeFactsReader reader, CapturingLoggerProvider logs)
        {
            Provider = provider;
            Policies = policies;
            Reader = reader;
            this.logs = logs;
            circuit = provider.CreateAsyncScope();
        }

        public ServiceProvider Provider { get; }

        public TestSourceAuthorizationService Policies { get; }

        public FakeFactsReader Reader { get; }

        public IReplicationService Service => circuit.ServiceProvider.GetRequiredService<IReplicationService>();

        public IReadOnlyList<StudioActionLogEntry> Ring => Provider.GetRequiredService<StudioActionLogService>().GetLatest();

        public IReadOnlyList<string> Logs => [.. logs.Entries.Where(static x => x.Level > Microsoft.Extensions.Logging.LogLevel.Debug).Select(static x => x.Message)];

        public async Task<string> IdentityAsync()
        {
            await using AsyncServiceScope scope = Provider.CreateAsyncScope();
            ResolvedSource resolved = await scope.ServiceProvider.GetRequiredService<StudioSourceResolver>().ResolveAsync(Scope, null, Token);
            return resolved.Target.Identity;
        }

        public static Harness Create(Action<PostgresStudioOptions> configure, bool policies = false)
        {
            TestAuthenticationStateProvider users = new();
            users.SignIn("operator");

            TestSourceAuthorizationService authorization = new();
            FakeFactsReader reader = new();
            CapturingLoggerProvider logs = new();

            ServiceCollection services = new();
            services.AddLogging(builder => builder.AddProvider(logs).SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Trace));
            services.AddSingleton(NpgsqlDataSource.Create(Refused));
            services.AddPostgresStudio(options =>
            {
                if (policies)
                {
                    options.SourceAuthorizationPolicy = "source-policy";
                    options.WriteAuthorizationPolicy = "write-policy";
                }

                configure(options);
            });

            services.AddSingleton<IAuthorizationService>(authorization);
            services.AddScoped<AuthenticationStateProvider>(_ => users);
            services.AddSingleton<IReplicationFactsReader>(reader);

            return new Harness(services.BuildServiceProvider(), authorization, reader, logs);
        }

        public async ValueTask DisposeAsync()
        {
            await circuit.DisposeAsync();

            foreach (NpgsqlDataSource dataSource in Provider.GetServices<NpgsqlDataSource>())
            {
                await dataSource.DisposeAsync();
            }

            await Provider.DisposeAsync();
        }
    }
}
