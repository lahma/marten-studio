using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Npgsql;

using PostgresStudio.Internal.Sql;
using PostgresStudio.Services;
using PostgresStudio.Services.Connection;
using PostgresStudio.Services.Database;
using PostgresStudio.Services.Diagnostics;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Health.Checks;
using PostgresStudio.Services.Health.Export;
using PostgresStudio.Services.Settings;
using PostgresStudio.Services.Sources;

namespace PostgresStudio.Tests.Connection;

/// <summary>
/// Issue #56: C18 counts the pools of data sources the host <em>named</em>. Npgsql reports a named data source's pool to its
/// meter under the name, not the connection string, so the name alone says nothing; the studio identifies it through the data
/// sources it already resolves - the one swept, the registered and listed sources - and says "not judged" for a named pool it
/// cannot identify, instead of "nothing found". The pool names come from a meter of the test's own, the data sources are real
/// <see cref="NpgsqlDataSource" />s that never connect, and the sources are the studio's real provider.
/// </summary>
public sealed class NamedPoolSiblingsTests
{
    private const string Own = "Host=127.0.0.1;Port=1;Database=orders;Username=app;Timeout=1";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static NpgsqlDataSource Source(string? name, string connectionString) =>
        new NpgsqlDataSourceBuilder(connectionString) { Name = name }.Build();

    /// <summary>The name Npgsql reports <paramref name="dataSource" />'s pool under - the connection string, unless it was named.</summary>
    private static string PoolOf(NpgsqlDataSource dataSource) =>
        NamedPoolIdentities.NameOf(dataSource) ?? throw new InvalidOperationException("The restored Npgsql has no readable data source name.");

    // ----------------------------------------------------------------------------------------------
    // The counting
    // ----------------------------------------------------------------------------------------------

    [Fact]
    public void A_named_pool_with_an_identity_counts_like_an_unnamed_pool_of_that_identity()
    {
        string identity = ConnectionPoolSiblings.IdentityOf(Own)!;
        NpgsqlPoolReading[] readings =
        [
            new(Own, 0, 0, 100),
            new("scheduler", 0, 0, 20),
            new("somewhere-else", 0, 0, 50),
        ];

        ConnectionPoolSiblings siblings = ConnectionPoolSiblings.Count(
            identity,
            readings,
            new Dictionary<string, string>
            {
                ["scheduler"] = identity,
                ["somewhere-else"] = ConnectionPoolSiblings.IdentityOf("Host=127.0.0.1;Port=1;Database=other;Username=app")!,
            });

        siblings.Should().Be(new ConnectionPoolSiblings(2, 120, 0), "the other database's named pool is identified and not counted, and is not unidentified either");
    }

    [Fact]
    public void A_named_pool_nobody_identifies_stays_unidentified_and_is_counted_as_such()
    {
        string identity = ConnectionPoolSiblings.IdentityOf(Own)!;
        NpgsqlPoolReading[] readings = [new(Own, 0, 0, 100), new("ghost", 0, 0, 100), new("scheduler", 0, 0, 100)];

        ConnectionPoolSiblings.NamedPools(readings).Should().BeEquivalentTo("ghost", "scheduler");

        ConnectionPoolSiblings.Count(identity, readings).Should().Be(new ConnectionPoolSiblings(1, 100, 2), "as before the identification");
        ConnectionPoolSiblings.Count(identity, readings, new Dictionary<string, string> { ["scheduler"] = identity })
            .Should().Be(new ConnectionPoolSiblings(2, 200, 1));
    }

    // ----------------------------------------------------------------------------------------------
    // The verdict
    // ----------------------------------------------------------------------------------------------

    private static HealthVerdict Verdict(ConnectionFacts facts) =>
        new SiblingPoolsCheck().Evaluate(facts, HealthEvaluation.At(ConnectionCheckFacts.At));

    [Theory]
    [InlineData(0, 1, "Not judged for 1 named pool(s) of this process that the studio cannot identify", "no pool it can identify connects to this database as this login.")]
    [InlineData(1, 3, "Not judged for 3 named pool(s) of this process that the studio cannot identify", "one pool of this process connects to this database as this login.")]
    public void A_quiet_verdict_with_unidentified_named_pools_says_it_did_not_judge_them(int pools, int unidentified, string first, string second)
    {
        HealthVerdict verdict = Verdict(ConnectionCheckFacts.Facts("Host=db.example", siblings: new ConnectionPoolSiblings(pools, pools == 0 ? null : 100, unidentified)));

        verdict.Findings.Should().BeEmpty("quiet, not unavailable: no variant, no new code");
        verdict.Note.Should().Contain(first).And.Contain("a data source named with NpgsqlDataSourceBuilder.Name that the studio does not resolve").And.EndWith(second);
    }

    [Fact]
    public void With_every_pool_identified_the_quiet_verdicts_are_as_they_were()
    {
        Verdict(ConnectionCheckFacts.Facts("Host=db.example", siblings: new ConnectionPoolSiblings(1, 100, 0))).Note
            .Should().Be("Only one Npgsql pool in this process connects to this database as this login.");
        Verdict(ConnectionCheckFacts.Facts("Host=db.example", siblings: new ConnectionPoolSiblings(0, null, 0))).Note
            .Should().StartWith("No Npgsql pool of this process that connects to this database as this login reports to Npgsql's meter")
            .And.Contain("pooling is off, or the pools are named or multi-host", "the hedge stays: a named or multi-host pool the meter reports under another name is not this one");
    }

    [Fact]
    public void The_quiet_note_gives_the_reasons_that_are_true_and_counts_pools_not_names()
    {
        Verdict(ConnectionCheckFacts.Facts("Host=db.example", siblings: new ConnectionPoolSiblings(1, 100, 1))).Note
            .Should().NotContain("naming no host").And.NotContain("in time");

        // A hostless pool is not a named pool: it is counted and said apart.
        Verdict(ConnectionCheckFacts.Facts("Host=db.example", siblings: new ConnectionPoolSiblings(1, 100, 3, NoHost: 2))).Note
            .Should().Contain("Not judged for 1 named pool(s)").And.Contain("and for 2 pool(s) whose name is a connection string naming no host").And.NotContain("in time");

        Verdict(ConnectionCheckFacts.Facts("Host=db.example", siblings: new ConnectionPoolSiblings(1, 100, 2, NoHost: 2))).Note
            .Should().StartWith("Not judged for 2 pool(s) whose name is a connection string naming no host").And.NotContain("named pool");

        Verdict(ConnectionCheckFacts.Facts("Host=db.example", siblings: new ConnectionPoolSiblings(1, 100, 3, CutOff: 2))).Note
            .Should().Contain("2 of them not judged because the studio could not identify them in time");
    }

    [Fact]
    public void Several_reporters_under_one_identified_name_are_not_judged_and_a_hostless_connection_string_is_counted_as_such()
    {
        string identity = ConnectionPoolSiblings.IdentityOf(Own)!;
        NpgsqlPoolReading[] readings =
        [
            new(Own, 0, 0, 100),
            new("shared-name", 0, 0, 100, Reporters: 3),
            new("Database=orders;Username=app", 0, 0, 100, Reporters: 2),
        ];

        ConnectionPoolSiblings siblings = ConnectionPoolSiblings.Count(
            identity,
            readings,
            new Dictionary<string, string> { ["shared-name"] = identity });

        siblings.Should().Be(new ConnectionPoolSiblings(1, 100, 5, NoHost: 2), "pools, not names: three under the shared name and two without a host");
        ConnectionPoolSiblings.NamedPools(readings).Should().BeEquivalentTo("shared-name");
    }

    [Fact]
    public void A_finding_counts_the_named_pools_it_could_not_identify_under_its_new_label()
    {
        HealthFinding finding = Verdict(ConnectionCheckFacts.Facts("Host=db.example", siblings: new ConnectionPoolSiblings(2, 200, 1))).Findings.Single();

        finding.Evidence.Should().Contain(x => x.Label == "Named pools not identified, left out" && x.Value == "1");
        finding.Caveat.Should().Contain("counted when the studio resolves it").And.Contain("(1 here)");
    }

    // ----------------------------------------------------------------------------------------------
    // The read, through the studio's own source provider
    // ----------------------------------------------------------------------------------------------

    /// <summary>The facts the sweep reads, and what the studio logged while it did, at Trace.</summary>
    private sealed record Read(ConnectionFacts Facts, string Logs);

    /// <summary>
    /// Reads <see cref="ConnectionFacts" /> for <paramref name="target" /> in a container with the studio's real source provider,
    /// where Npgsql's meter is a meter of the test's own that reports <paramref name="reported" /> as the process's pools.
    /// </summary>
    private static async Task<Read> ReadAsync(
        NpgsqlDataSource target,
        string[] reported,
        Action<PostgresDiagnosticsOptions>? configure = null,
        Action<IServiceCollection>? services = null)
    {
        string meterName = "PostgresStudio.Tests.Named." + Guid.NewGuid().ToString("N");
        using Meter meter = new(meterName);

        NpgsqlPoolInstruments names = NpgsqlPoolInstruments.UpToNpgsql9;
        meter.CreateObservableUpDownCounter(names.Usage, () => reported.SelectMany(pool => new[]
        {
            new Measurement<int>(0, new KeyValuePair<string, object?>(names.PoolNameTag, pool), new KeyValuePair<string, object?>(names.StateTag, "idle")),
            new Measurement<int>(0, new KeyValuePair<string, object?>(names.PoolNameTag, pool), new KeyValuePair<string, object?>(names.StateTag, "used")),
        }));
        meter.CreateObservableUpDownCounter(names.Max, () => reported.Select(pool => new Measurement<int>(100, new KeyValuePair<string, object?>(names.PoolNameTag, pool))));

        CapturingProvider logs = new();

        ServiceCollection collection = new();
        collection.AddLogging(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
        collection.AddSingleton(new SessionPoolObserver(meterName));
        collection.AddSingleton(target);
        services?.Invoke(collection);
        collection.AddPostgresDiagnostics(configure);

        await using ServiceProvider provider = collection.BuildServiceProvider();
        await using AsyncServiceScope scope = provider.CreateAsyncScope();

        DataSourceOnly database = new(target);
        ResolvedSource resolved = new(new StudioScope("default", "default", null), new StudioSourceRegistration("default", "Default", StudioSourceKind.DataSource), database);
        await using HealthConnection connection = new(database);

        HealthCheckContext context = new(
            resolved,
            scope.ServiceProvider.GetRequiredService<IDiagnosticsSettings>(),
            NoStudioRuntime.Instance,
            scope.ServiceProvider,
            new HealthSqlRunner(connection, new ReadOnlySqlOptions(), null),
            ConnectionCheckFacts.At,
            new HealthSweepMemo(),
            Token);

        ConnectionFacts facts = await ConnectionFacts.ReadAsync(context, Token);
        facts.Siblings.Should().NotBeNull("the observer is the test's, and the data source's identity parses");

        return new Read(facts, logs.Text());
    }

    [Fact]
    public async Task A_named_source_the_studio_lists_beside_an_unnamed_one_to_the_same_identity_is_two_pools()
    {
        await using NpgsqlDataSource unnamed = Source(null, Own);
        await using NpgsqlDataSource scheduler = Source("scheduler-pool", Own + ";Application Name=scheduler");

        // A keyed registration, listed under a name of the studio's own: the way a host hands the studio a source of its own.
        Read read = await ReadAsync(
            unnamed,
            [PoolOf(unnamed), "scheduler-pool"],
            configure: o => o.AddDataSource("scheduler", sp => sp.GetRequiredKeyedService<NpgsqlDataSource>("scheduler")),
            services: s => s.AddKeyedSingleton("scheduler", scheduler));

        read.Facts.Siblings.Should().Be(new ConnectionPoolSiblings(2, 200, 0));

        HealthFinding finding = Verdict(read.Facts).Findings.Should().ContainSingle().Subject;
        finding.Evidence.Should().Contain(x => x.Label == "Pools to this database as this login" && x.Value == "2");
        finding.Evidence.Should().NotContain(static x => x.Label.StartsWith("Named pools", StringComparison.Ordinal), "none was left out");
    }

    [Fact]
    public async Task A_named_source_the_studio_does_not_resolve_is_not_judged_and_the_verdict_says_so()
    {
        await using NpgsqlDataSource unnamed = Source(null, Own);
        await using NpgsqlDataSource hidden = Source("kept-to-itself", Own + ";Application Name=hidden");

        Read read = await ReadAsync(unnamed, [PoolOf(unnamed), PoolOf(hidden)]);

        read.Facts.Siblings.Should().Be(new ConnectionPoolSiblings(1, 100, 1));

        HealthVerdict verdict = Verdict(read.Facts);
        verdict.Findings.Should().BeEmpty();
        verdict.Note.Should().Contain("Not judged for 1 named pool(s)").And.EndWith("one pool of this process connects to this database as this login.");
    }

    [Fact]
    public async Task Of_two_named_pools_the_one_the_studio_resolves_is_counted_and_the_other_is_not_judged()
    {
        await using NpgsqlDataSource unnamed = Source(null, Own);
        await using NpgsqlDataSource listed = Source("listed-pool", Own + ";Application Name=listed");
        await using NpgsqlDataSource hidden = Source("kept-to-itself", Own + ";Application Name=hidden");

        Read read = await ReadAsync(
            unnamed,
            [PoolOf(unnamed), PoolOf(listed), PoolOf(hidden)],
            configure: o => o.AddDataSource("listed", _ => listed));

        read.Facts.Siblings.Should().Be(new ConnectionPoolSiblings(2, 200, 1));

        HealthFinding finding = Verdict(read.Facts).Findings.Should().ContainSingle().Subject;
        finding.Evidence.Should().Contain(x => x.Label == "Named pools not identified, left out" && x.Value == "1");
    }

    [Fact]
    public async Task The_data_source_being_swept_is_identified_when_the_host_named_it()
    {
        // A host that sets NpgsqlDataSourceBuilder.Name on the data source the studio reads: its own pool reports a name, and used
        // to be "not found" - zero pools to the very database being read.
        await using NpgsqlDataSource named = Source("orders-db", Own);

        Read read = await ReadAsync(named, [PoolOf(named)]);

        read.Facts.Siblings.Should().Be(new ConnectionPoolSiblings(1, 100, 0));
    }

    [Fact]
    public async Task A_named_pool_to_another_database_is_identified_and_is_neither_counted_nor_unidentified()
    {
        await using NpgsqlDataSource unnamed = Source(null, Own);
        await using NpgsqlDataSource other = Source("reports-db", "Host=127.0.0.1;Port=1;Database=reports;Username=app;Timeout=1");

        Read read = await ReadAsync(unnamed, [PoolOf(unnamed), PoolOf(other)], configure: o => o.AddDataSource("reports", _ => other));

        read.Facts.Siblings.Should().Be(new ConnectionPoolSiblings(1, 100, 0));
    }

    [Fact]
    public async Task A_hosts_database_source_is_never_asked_for_a_data_source_by_the_identification()
    {
        // IPostgresDatabaseSource promises the studio asks only for a database somebody opens or a sweep reaches: a host that builds
        // lazily would have every tenant's data source built by the first sweep.
        await using NpgsqlDataSource unnamed = Source(null, Own);
        await using NpgsqlDataSource tenant = Source("tenant-a-pool", Own + ";Application Name=a");
        HostDatabases tenants = new(("a", tenant));

        Read read = await ReadAsync(unnamed, [PoolOf(unnamed), "tenant-a-pool"], configure: o => o.AddDatabaseSource("tenants", _ => tenants));

        read.Facts.Siblings.Should().Be(new ConnectionPoolSiblings(1, 100, 1), "the tenant's named pool is not judged");
        tenants.Opened.Should().BeEmpty("GetDataSourceAsync is never called by the walk");
        tenants.Listed.Should().Be(0, "nor is the list read, nor the host's factory called");
    }

    [Fact]
    public async Task Nothing_is_asked_of_the_studios_sources_when_every_pool_name_is_a_connection_string()
    {
        await using NpgsqlDataSource unnamed = Source(null, Own);
        await using NpgsqlDataSource sibling = Source(null, Own + ";Application Name=sibling");
        CountingProvider provider = new();

        Read read = await ReadAsync(unnamed, [PoolOf(unnamed), PoolOf(sibling)], services: s => s.AddScoped<IStudioSourceProvider>(_ => provider));

        read.Facts.Siblings.Should().Be(new ConnectionPoolSiblings(2, 200, 0));
        provider.Walks.Should().Be(0, "the identification is asked for only when a pool name did not parse");
    }

    [Fact]
    public async Task A_source_that_fails_leaves_what_was_identified_standing_and_the_rest_unidentified()
    {
        await using NpgsqlDataSource unnamed = Source(null, Own);
        await using NpgsqlDataSource listed = Source("listed-pool", Own + ";Application Name=listed");

        Read read = await ReadAsync(
            unnamed,
            [PoolOf(unnamed), PoolOf(listed), "ghost"],
            configure: o => o
                .AddDataSource("listed", _ => listed)
                .AddDataSource("broken", _ => throw new InvalidOperationException("S3NT1NEL factory failure")));

        read.Facts.Siblings.Should().Be(new ConnectionPoolSiblings(2, 200, 1));
    }

    [Fact]
    public async Task A_hosts_own_cancellation_inside_the_walk_is_contained_and_the_connection_facts_still_read()
    {
        await using NpgsqlDataSource unnamed = Source(null, Own);
        CountingProvider provider = new() { Throws = new TaskCanceledException("the host's own cancellation") };

        Read read = await ReadAsync(unnamed, [PoolOf(unnamed), "ghost"], services: s => s.AddScoped<IStudioSourceProvider>(_ => provider));

        provider.Walks.Should().Be(1);
        read.Facts.Settings.Readable.Should().BeTrue();
        read.Facts.Siblings.Should().Be(new ConnectionPoolSiblings(1, 100, 1), "not judged, and not 'in time': the bound did not run out");
    }

    [Fact]
    public async Task A_walk_that_outlasts_QueryTimeout_stops_and_says_the_pools_it_did_not_reach_were_not_identified_in_time()
    {
        await using NpgsqlDataSource unnamed = Source(null, Own);
        CountingProvider provider = new() { Hangs = true };

        Read read = await ReadAsync(
            unnamed,
            [PoolOf(unnamed), "ghost-a", "ghost-b"],
            configure: o => o.QueryTimeout = TimeSpan.FromSeconds(1),
            services: s => s.AddScoped<IStudioSourceProvider>(_ => provider));

        read.Facts.Siblings.Should().Be(new ConnectionPoolSiblings(1, 100, 2, CutOff: 2));
        Verdict(read.Facts).Note.Should().Contain("2 of them not judged because the studio could not identify them in time");
    }

    [Fact]
    public async Task A_walk_stops_after_the_attempt_cap_and_says_so()
    {
        await using NpgsqlDataSource unnamed = Source(null, Own);
        await using NpgsqlDataSource other = Source("someone-elses", "Host=127.0.0.1;Port=1;Database=reports;Username=app;Timeout=1");
        CountingProvider provider = new() { Yields = other, YieldCount = NamedPoolIdentities.MaxExamined * 2 };

        Read read = await ReadAsync(unnamed, [PoolOf(unnamed), "ghost"], services: s => s.AddScoped<IStudioSourceProvider>(_ => provider));

        provider.Yielded.Should().Be(NamedPoolIdentities.MaxExamined + 1, "the cap is reached when a source beyond it shows up");
        read.Facts.Siblings.Should().Be(new ConnectionPoolSiblings(1, 100, 1, CutOff: 1));
    }

    [Fact]
    public async Task A_walk_whose_last_source_is_the_one_at_the_cap_is_not_cut_off()
    {
        await using NpgsqlDataSource unnamed = Source(null, Own);
        await using NpgsqlDataSource other = Source("someone-elses", "Host=127.0.0.1;Port=1;Database=reports;Username=app;Timeout=1");
        CountingProvider provider = new() { Yields = other, YieldCount = NamedPoolIdentities.MaxExamined };

        Read read = await ReadAsync(unnamed, [PoolOf(unnamed), "ghost"], services: s => s.AddScoped<IStudioSourceProvider>(_ => provider));

        provider.Yielded.Should().Be(NamedPoolIdentities.MaxExamined);
        read.Facts.Siblings.Should().Be(new ConnectionPoolSiblings(1, 100, 1), "every source was looked at: the ghost is simply not anybody's, and not 'in time'");
    }

    [Fact]
    public async Task Several_reporters_under_one_name_the_swept_source_among_them_are_not_judged()
    {
        // A factory that names every tenant's data source alike: three pools, one name, one tally in the meter.
        await using NpgsqlDataSource swept = Source("tenant-pool", Own);
        await using NpgsqlDataSource second = Source("tenant-pool", Own + ";Application Name=second");
        await using NpgsqlDataSource third = Source("tenant-pool", Own + ";Application Name=third");

        Read read = await ReadAsync(
            swept,
            ["tenant-pool", "tenant-pool", "tenant-pool"],
            configure: o => o.AddDataSource("second", _ => second).AddDataSource("third", _ => third));

        read.Facts.Siblings.Should().Be(new ConnectionPoolSiblings(0, null, 3));

        HealthVerdict verdict = Verdict(read.Facts);
        verdict.Findings.Should().BeEmpty("three pools under one name are no finding: they cannot be told apart");
        verdict.Note.Should().Contain("Not judged for 3 named pool(s)");
    }

    [Fact]
    public async Task A_named_multi_host_data_source_whose_every_per_host_pool_reports_its_name_is_not_a_finding()
    {
        await using NpgsqlMultiHostDataSource twoHosts = new NpgsqlDataSourceBuilder("Host=127.0.0.1,127.0.0.2;Port=1;Database=orders;Username=app;Timeout=1") { Name = "two-hosts" }.BuildMultiHost();

        NamedPoolIdentities.NameOf(twoHosts).Should().Be("two-hosts");

        Read read = await ReadAsync(twoHosts, ["two-hosts", "two-hosts"]);

        read.Facts.Siblings.Should().Be(new ConnectionPoolSiblings(0, null, 2));
        Verdict(read.Facts).Findings.Should().BeEmpty();
    }

    [Fact]
    public async Task The_connection_page_reader_over_the_root_provider_reads_with_scopes_validated()
    {
        // ConnectionFactsSource is a singleton over the root provider, and the source provider is scoped: with ValidateScopes (the
        // Development default) resolving it from the root throws. The walk takes a scope of its own.
        await using NpgsqlDataSource unnamed = Source(null, Own);
        await using NpgsqlDataSource listed = Source("listed-pool", Own + ";Application Name=listed");

        string meterName = "PostgresStudio.Tests.Named." + Guid.NewGuid().ToString("N");
        using Meter meter = new(meterName);
        NpgsqlPoolInstruments names = NpgsqlPoolInstruments.UpToNpgsql9;
        string[] reported = [PoolOf(unnamed), "listed-pool"];
        meter.CreateObservableUpDownCounter(names.Usage, () => reported.SelectMany(pool => new[]
        {
            new Measurement<int>(0, new KeyValuePair<string, object?>(names.PoolNameTag, pool), new KeyValuePair<string, object?>(names.StateTag, "idle")),
            new Measurement<int>(0, new KeyValuePair<string, object?>(names.PoolNameTag, pool), new KeyValuePair<string, object?>(names.StateTag, "used")),
        }));
        meter.CreateObservableUpDownCounter(names.Max, () => reported.Select(pool => new Measurement<int>(100, new KeyValuePair<string, object?>(names.PoolNameTag, pool))));

        ServiceCollection collection = new();
        collection.AddLogging();
        collection.AddSingleton(new SessionPoolObserver(meterName));
        collection.AddSingleton(unnamed);
        collection.AddPostgresStudio(o => o.AddDataSource("listed", _ => listed));

        await using ServiceProvider root = collection.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = false });

        Action rootResolution = () => root.GetRequiredService<IStudioSourceProvider>();
        rootResolution.Should().Throw<InvalidOperationException>("the premise: the source provider is scoped");

        DataSourceOnly database = new(unnamed);
        ResolvedSource resolved = new(new StudioScope("default", "default", null), new StudioSourceRegistration("default", "Default", StudioSourceKind.DataSource), database);

        ConnectionFacts facts = await root.GetRequiredService<PostgresStudio.Services.Connection.IConnectionFactsSource>().GetAsync(resolved, Token);

        facts.Settings.Readable.Should().BeTrue();
        facts.Siblings.Should().Be(
            new ConnectionPoolSiblings(2, 200, 0),
            "the named pool is identified through the provider of the walk's own scope: resolved from the root it would throw, be contained, and leave the pool not judged");
    }

    [Fact]
    public async Task A_pool_name_that_is_a_connection_string_naming_no_host_is_not_judged()
    {
        await using NpgsqlDataSource unnamed = Source(null, Own);

        Read read = await ReadAsync(unnamed, [PoolOf(unnamed), "Database=orders;Username=app"]);

        read.Facts.Siblings.Should().Be(new ConnectionPoolSiblings(1, 100, 1, NoHost: 1));
        Verdict(read.Facts).Note.Should().StartWith("Not judged for 1 pool(s) whose name is a connection string naming no host").And.NotContain("named pool");
    }

    [Fact]
    public async Task The_restored_Npgsql_has_a_data_source_name_the_studio_can_read_and_a_builder_name_reaches_it()
    {
        // Name is internal in Npgsql 9.0.4 and 10.0.3: if a release renames it, every named pool would silently go unidentified.
        NamedPoolIdentities.CanReadNames.Should().BeTrue();

        await using NpgsqlDataSource named = Source("a-readable-name", Own);
        await using NpgsqlDataSource unnamed = Source(null, Own);

        NamedPoolIdentities.NameOf(named).Should().Be("a-readable-name");
        NamedPoolIdentities.NameOf(unnamed).Should().StartWith("Host=127.0.0.1", "an unnamed data source reports its connection string");
        ConnectionPoolSiblings.IdentityOf(NamedPoolIdentities.NameOf(unnamed)).Should().Be(ConnectionPoolSiblings.IdentityOf(Own));
    }

    // ----------------------------------------------------------------------------------------------
    // D33: nothing the map holds leaves the reader
    // ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task No_pool_name_connection_string_host_or_password_of_a_named_source_reaches_the_facts_the_finding_the_export_or_the_logs()
    {
        const string Name = "S3NT1NEL-pool-name";
        const string Host = "s3nt1nel-host.invalid";
        const string Password = "S3NT1NEL-password";
        const string Ghost = "S3NT1NEL-unresolved-name";

        string sentinelString = $"Host={Host};Port=5999;Database=sentinel_db;Username=sentinel_user;Password={Password};Persist Security Info=true;Timeout=1";

        await using NpgsqlDataSource target = Source("S3NT1NEL-target-name", sentinelString);
        await using NpgsqlDataSource sibling = Source(Name, sentinelString + ";Application Name=S3NT1NEL-app");

        Read read = await ReadAsync(
            target,
            [PoolOf(target), PoolOf(sibling), Ghost],
            configure: o => o
                .AddDataSource("sibling", _ => sibling)
                .AddDataSource("broken", _ => throw new InvalidOperationException("the host's factory failed")));

        // The broken source is the studio's own warning (9210), so the captured logs are not empty.
        read.Facts.Siblings.Should().Be(new ConnectionPoolSiblings(2, 200, 1), "the sentinel sources are identified and counted, the ghost is not");

        HealthFinding finding = Verdict(read.Facts).Findings.Should().ContainSingle().Subject;
        HealthVerdict quiet = Verdict(read.Facts with { Siblings = new ConnectionPoolSiblings(1, 100, 1) });

        HealthCheckResult result = new(
            new HealthCheckInfo("C18", HealthArea.Connections, HealthCostTier.Catalog, HealthGate.Database, "A check", null, typeof(object)),
            HealthCheckOutcome.Checked,
            [finding],
            quiet.Note,
            null,
            [],
            TimeSpan.Zero);
        HealthSweep sweep = new("default", "db", HealthSweepKind.Regular, ConnectionCheckFacts.At, TimeSpan.Zero, [result]);
        HealthExportSummary summary = HealthExportSummary.From(ConnectionCheckFacts.At, [sweep]);

        // The snapshot the diagnostics API builds from the same results, through the host gate.
        (IReadOnlyList<PostgresFinding> snapshotFindings, IReadOnlyList<PostgresCheck> snapshotChecks, int _) = DiagnosticsSnapshotBuilder.Findings(
            [result],
            new HostGate(includeActivity: true, StatementScreen.Unchecked(hidesDocumentTypes: false)),
            NoStudioRuntime.Instance,
            "default",
            new StudioScope("default", "default", null));

        snapshotFindings.Should().ContainSingle("the finding is in the snapshot, so the dump is not vacuous");

        JsonSerializerOptions json = new() { IncludeFields = true, MaxDepth = 64 };

        // The whole of the facts - every field, not only the counts - without the exception a refused connection kept for the checks to
        // throw again: that is the driver's, it names the host it could not reach, and no page, finding or log shows it. The facts
        // show the swept data source's own host, database and login (the Connection page's job), so what they may not hold is the
        // pool names, the sibling's Application Name and every password.
        ConnectionFacts wholeFacts = read.Facts with { ObservedFailure = null };
        string factsDump = JsonSerializer.Serialize(wholeFacts, json) + "\n" + wholeFacts.ObservedNotRead;

        foreach (string sentinel in (string[]) [Name, Ghost, "S3NT1NEL-target-name", "S3NT1NEL-app", Password])
        {
            factsDump.Should().NotContain(sentinel);
        }

        string everything = string.Join(
            '\n',
            JsonSerializer.Serialize(read.Facts.Siblings, json),
            JsonSerializer.Serialize(finding, json),
            ConnectionCheckFacts.Everything(finding),
            quiet.Note,
            summary.Describe(TimeSpan.FromSeconds(60)),
            JsonSerializer.Serialize(summary.ToData(), json),
            JsonSerializer.Serialize(summary, json),
            JsonSerializer.Serialize(snapshotFindings, json),
            JsonSerializer.Serialize(snapshotChecks, json),
            read.Logs);

        factsDump.Should().Contain("Settings", "the facts dump is not vacuous");
        everything.Should().Contain("Pools to this database as this login", "the dump is not vacuous");
        read.Logs.Should().NotBeEmpty("the studio logged at Trace while it read, so the capture is not vacuous");

        foreach (string sentinel in (string[]) [Name, Ghost, Host, Password, "S3NT1NEL", "sentinel_db", "sentinel_user", "Host="])
        {
            everything.Should().NotContain(sentinel);
        }
    }

    // ----------------------------------------------------------------------------------------------
    // Fakes
    // ----------------------------------------------------------------------------------------------

    private sealed class DataSourceOnly(NpgsqlDataSource dataSource) : IStudioDatabase
    {
        public string Identity => "default";

        public string Name => "orders";

        public string Server => "127.0.0.1:1";

        public NpgsqlDataSource? DataSource => dataSource;

        public NpgsqlConnection CreateConnection(StudioConnectionUsage usage) => dataSource.CreateConnection();
    }

    /// <summary>A source provider that holds one stated data source, repeatedly, or throws, or never answers - and counts its walks.</summary>
    private sealed class CountingProvider : IStudioSourceProvider
    {
        private int walks;
        private int yielded;

        public Exception? Throws { get; init; }

        public bool Hangs { get; init; }

        public NpgsqlDataSource? Yields { get; init; }

        public int YieldCount { get; init; }

        public int Walks => Volatile.Read(ref walks);

        public int Yielded => Volatile.Read(ref yielded);

        public long Generation => 0;

        public IReadOnlyList<StudioSourceRegistration> Registrations() => [];

        public StudioSourceRegistration? Find(string? key) => null;

        public StoreAvailability TryResolve(StudioSourceRegistration registration, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IStudioSource? source)
        {
            source = null;
            return StoreAvailability.Unavailable("not used", DateTimeOffset.UnixEpoch);
        }

        public void Invalidate()
        {
        }

        public IAsyncEnumerable<StudioListedDatabase> ListUnattendedAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<StudioUnattendedResolution> ResolveUnattendedAsync(string sourceKey, string databaseId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<NpgsqlDataSource> HeldDataSourcesAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref walks);

            if (Throws is not null)
            {
                throw Throws;
            }

            if (Hangs)
            {
                await new TaskCompletionSource().Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            for (int i = 0; Yields is not null && i < YieldCount; i++)
            {
                Interlocked.Increment(ref yielded);
                yield return Yields;
                await Task.Yield();
            }
        }
    }

    /// <summary>A host's database source over stated data sources, recording which it was asked to hand over.</summary>
    private sealed class HostDatabases(params (string Name, NpgsqlDataSource DataSource)[] databases) : IPostgresDatabaseSource
    {
        private readonly ConcurrentQueue<string> opened = new();
        private int listed;

        public IReadOnlyCollection<string> Opened => opened;

        public int Listed => Volatile.Read(ref listed);

        public async IAsyncEnumerable<string> ListAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref listed);

            foreach ((string name, _) in databases)
            {
                await Task.Yield();
                yield return name;
            }
        }

        public ValueTask<NpgsqlDataSource?> GetDataSourceAsync(string name, CancellationToken cancellationToken)
        {
            opened.Enqueue(name);
            return ValueTask.FromResult<NpgsqlDataSource?>(databases.FirstOrDefault(x => x.Name == name).DataSource);
        }
    }

    private sealed class CapturingProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> lines = new();

        public string Text() => string.Join('\n', lines);

        public ILogger CreateLogger(string categoryName) => new Capture(lines, categoryName);

        public void Dispose()
        {
        }

        private sealed class Capture(ConcurrentQueue<string> lines, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                lines.Enqueue(category + ": " + formatter(state, exception) + (exception is null ? string.Empty : " " + exception));
        }
    }
}
