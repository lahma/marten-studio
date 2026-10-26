using System.Data.Common;
using System.Runtime.CompilerServices;

using MartenStudio.Tests.Support;

using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Npgsql;

using PostgresStudio.Services;
using PostgresStudio.Services.Postgres;
using PostgresStudio.Services.Settings;
using PostgresStudio.Services.Sources;
using PostgresStudio.Tests.Support;

namespace PostgresStudio.Tests.Sources;

/// <summary>
/// PostgresStudio's sources over DI descriptors: the unkeyed singleton data source is <c>default</c>, a keyed one only
/// through <c>AddDataSource</c>, a host's database source after them; what each is named, how many databases it has,
/// that nothing is disposed, what a host's list leaves out, its caches and timeouts, and that no scope without a
/// database is ever completed.
/// </summary>
/// <remarks>
/// <c>AddNpgsqlDataSource</c> is Npgsql.DependencyInjection's, which is not in the package budget; its shape is
/// reproduced here as PS-0 measured it (E7): factories only, an unkeyed <c>NpgsqlDataSource</c> singleton beside a
/// <c>DbDataSource</c> forward and a transient <c>NpgsqlConnection</c>, and with a service key the same, keyed.
/// </remarks>
public class PostgresStudioSourceProviderTests
{
    private const string Cs = "Host=db.example;Port=1;Database=app;Username=app_user;Password=secret;Timeout=1";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>What <c>AddNpgsqlDataSource(cs)</c> registers, unkeyed or keyed.</summary>
    private static void AddNpgsqlDataSourceShape(IServiceCollection services, string connectionString, object? key = null)
    {
        if (key is null)
        {
            services.AddSingleton<NpgsqlDataSource>(_ => NpgsqlDataSource.Create(connectionString));
            services.AddSingleton<DbDataSource>(static sp => sp.GetRequiredService<NpgsqlDataSource>());
            services.AddTransient<NpgsqlConnection>(static sp => sp.GetRequiredService<NpgsqlDataSource>().CreateConnection());
            services.AddTransient<DbConnection>(static sp => sp.GetRequiredService<NpgsqlConnection>());
        }
        else
        {
            services.AddKeyedSingleton<NpgsqlDataSource>(key, (_, _) => NpgsqlDataSource.Create(connectionString));
            services.AddKeyedSingleton<DbDataSource>(key, static (sp, k) => sp.GetRequiredKeyedService<NpgsqlDataSource>(k));
            services.AddKeyedTransient<NpgsqlConnection>(key, static (sp, k) => sp.GetRequiredKeyedService<NpgsqlDataSource>(k).CreateConnection());
        }
    }

    private sealed class Harness : IAsyncDisposable
    {
        public Harness(IServiceCollection services, PostgresStudioOptions options, TimeProvider? time = null, ILogger<PostgresStudioSourceProvider>? logger = null)
        {
            Options = options;
            Root = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
            Registry = new PostgresSourceRegistry(services, Root, time);
            Provider = new PostgresStudioSourceProvider(
                Microsoft.Extensions.Options.Options.Create(options),
                Registry,
                logger ?? NullLogger<PostgresStudioSourceProvider>.Instance);
        }

        public PostgresStudioOptions Options { get; }

        public PostgresSourceRegistry Registry { get; }

        public ServiceProvider Root { get; }

        public PostgresStudioSourceProvider Provider { get; }

        public IStudioSource Resolve(string key)
        {
            StudioSourceRegistration registration = Provider.Find(key) ?? throw new InvalidOperationException("No " + key);
            Provider.TryResolve(registration, out IStudioSource? source).IsAvailable.Should().BeTrue();
            return source!;
        }

        public StoreAvailability TryResolve(string key) => Provider.TryResolve(Provider.Find(key)!, out _);

        public async ValueTask DisposeAsync() => await Root.DisposeAsync();
    }

    [Fact]
    public async Task The_unkeyed_singleton_data_source_is_default_and_is_one_database_named_default()
    {
        ServiceCollection services = new();
        AddNpgsqlDataSourceShape(services, Cs);
        AddNpgsqlDataSourceShape(services, Cs, key: "k");

        await using Harness harness = new(services, new PostgresStudioOptions());

        harness.Provider.Registrations().Should().Equal(new StudioSourceRegistration("default", "Default", StudioSourceKind.DataSource));

        IStudioSource source = harness.Resolve("DEFAULT");
        source.Cardinality.Should().Be(StudioDatabaseCardinality.Single);
        source.ShowsTenantSelector.Should().BeFalse();

        IStudioDatabase database = (await source.DatabasesAsync(Token)).Should().ContainSingle().Subject;
        database.Identity.Should().Be("default", "a DI data source's identity is its registration name");
        database.Name.Should().Be("app");
        database.Server.Should().Be("db.example");
        database.DataSource.Should().BeSameAs(harness.Root.GetRequiredService<NpgsqlDataSource>());
        database.DataSource!.ConnectionString.Should().NotContain("secret", "Npgsql hands back the connection string without the password");
    }

    [Fact]
    public async Task A_scoped_or_transient_unkeyed_data_source_is_not_a_source_and_says_so_once()
    {
        CapturingLoggerProvider logs = new();
        using ILoggerFactory factory = logs.CreateFactory();

        ServiceCollection scoped = new();
        scoped.AddScoped<NpgsqlDataSource>(_ => NpgsqlDataSource.Create(Cs));
        await using Harness first = new(scoped, new PostgresStudioOptions(), logger: factory.CreateLogger<PostgresStudioSourceProvider>());
        first.Provider.Registrations().Should().BeEmpty();
        first.Provider.Registrations().Should().BeEmpty();

        logs.Entries.Where(static x => x.EventId.Id == 9300).Should().ContainSingle()
            .Which.Should().Match<CapturedLogEntry>(static x => x.Level == LogLevel.Information && x.Message.Contains("registered as Scoped"));

        ServiceCollection transient = new();
        transient.AddTransient<NpgsqlDataSource>(_ => NpgsqlDataSource.Create(Cs));
        await using Harness second = new(transient, new PostgresStudioOptions());
        second.Provider.Registrations().Should().BeEmpty();
    }

    [Fact]
    public async Task The_last_unkeyed_registration_decides_because_it_is_the_one_the_container_resolves()
    {
        ServiceCollection singletonThenTransient = new();
        singletonThenTransient.AddSingleton(NpgsqlDataSource.Create(Cs));
        singletonThenTransient.AddTransient<NpgsqlDataSource>(_ => NpgsqlDataSource.Create(Cs));
        await using Harness transientLast = new(singletonThenTransient, new PostgresStudioOptions());
        transientLast.Provider.Registrations().Should().BeEmpty("resolving the last registration would build a pool per resolution");

        ServiceCollection transientThenSingleton = new();
        transientThenSingleton.AddTransient<NpgsqlDataSource>(_ => NpgsqlDataSource.Create(Cs));
        transientThenSingleton.AddSingleton(NpgsqlDataSource.Create(Cs));
        await using Harness singletonLast = new(transientThenSingleton, new PostgresStudioOptions());
        singletonLast.Provider.Registrations().Select(static x => x.Key).Should().Equal("default");
        singletonLast.Resolve("default");
    }

    [Fact]
    public async Task IncludeDefaultDataSource_false_leaves_it_out()
    {
        ServiceCollection services = new();
        services.AddSingleton(NpgsqlDataSource.Create(Cs));

        await using Harness harness = new(services, new PostgresStudioOptions { IncludeDefaultDataSource = false });

        harness.Provider.Registrations().Should().BeEmpty();
        harness.Provider.Find("default").Should().BeNull();
    }

    [Fact]
    public async Task A_keyed_data_source_is_listed_only_through_AddDataSource_and_its_factory_is_called_once_with_the_root()
    {
        ServiceCollection services = new();
        services.AddSingleton(NpgsqlDataSource.Create(Cs));
        AddNpgsqlDataSourceShape(services, "Host=orders.example;Database=orders;Username=app", key: "orders");
        services.AddScoped<ScopedProbe>();

        int calls = 0;
        bool scopedResolvable = true;
        PostgresStudioOptions options = new PostgresStudioOptions().AddDataSource("orders", sp =>
        {
            calls++;

            // The root provider, not a circuit's scope: a scoped service is refused there (ValidateScopes is on).
            try
            {
                sp.GetRequiredService<ScopedProbe>();
            }
            catch (InvalidOperationException)
            {
                scopedResolvable = false;
            }

            return sp.GetRequiredKeyedService<NpgsqlDataSource>("orders");
        });

        await using Harness harness = new(services, options);

        harness.Provider.Registrations().Select(static x => x.Key).Should().Equal("default", "orders");

        IStudioSource orders = harness.Resolve("orders");
        IStudioSource again = harness.Resolve("Orders");

        calls.Should().Be(1, "the factory is called once and its answer kept");
        scopedResolvable.Should().BeFalse("the factory is handed the root provider");
        IStudioDatabase database = (await orders.DatabasesAsync(Token)).Should().ContainSingle().Subject;
        database.Identity.Should().Be("orders");
        database.Name.Should().Be("orders");
        database.DataSource.Should().BeSameAs(harness.Root.GetRequiredKeyedService<NpgsqlDataSource>("orders"));
        (await again.DatabasesAsync(Token)).Single().DataSource.Should().BeSameAs(database.DataSource);
    }

    [Fact]
    public async Task Concurrent_first_callers_call_a_factory_exactly_once()
    {
        ServiceCollection services = new();
        using ManualResetEventSlim release = new(false);
        using ManualResetEventSlim entered = new(false);
        NpgsqlDataSource reporting = NpgsqlDataSource.Create(Cs);
        int calls = 0;

        PostgresStudioOptions options = new PostgresStudioOptions().AddDataSource("reporting", _ =>
        {
            Interlocked.Increment(ref calls);
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(30));
            return reporting;
        });

        await using Harness harness = new(services, options);
        StudioSourceRegistration registration = harness.Provider.Find("reporting")!;

        Task<bool>[] callers =
        [
            .. Enumerable.Range(0, 4).Select(caller => Task.Run(() => harness.Provider.TryResolve(registration, out _).IsAvailable, Token)),
        ];

        entered.Wait(TimeSpan.FromSeconds(30), Token).Should().BeTrue();
        release.Set();

        (await Task.WhenAll(callers)).Should().OnlyContain(static x => x);
        calls.Should().Be(1, "every caller after the first waits for the one call");

        await reporting.DisposeAsync();
    }

    [Fact]
    public async Task A_factory_that_resolves_a_scoped_or_transient_data_source_is_refused_rather_than_kept_for_the_process()
    {
        ServiceCollection services = new();
        services.AddKeyedScoped<NpgsqlDataSource>("visitor", (_, _) => NpgsqlDataSource.Create(Cs));
        services.AddKeyedTransient<NpgsqlDataSource>("each", (_, _) => NpgsqlDataSource.Create(Cs));
        services.AddTransient<IPostgresDatabaseSource>(_ => new FakeDatabaseSource("acme"));

        PostgresStudioOptions options = new PostgresStudioOptions()
            .AddDataSource("visitor", static sp => sp.GetRequiredKeyedService<NpgsqlDataSource>("visitor"))
            .AddDataSource("each", static sp => sp.GetRequiredKeyedService<NpgsqlDataSource>("each"))
            .AddDatabaseSource("tenants", static sp => sp.GetRequiredService<IPostgresDatabaseSource>());

        await using Harness harness = new(services, options);

        StoreAvailability visitor = harness.TryResolve("visitor");
        visitor.IsAvailable.Should().BeFalse();
        visitor.Message.Should().Contain("NpgsqlDataSource under the key 'visitor' is registered as Scoped").And.Contain("register it as a singleton");

        harness.TryResolve("each").Message.Should().Contain("registered as Transient");
        harness.TryResolve("tenants").Message.Should().Contain("IPostgresDatabaseSource is registered as Transient");
    }

    [Fact]
    public async Task Sources_are_ordered_default_then_data_sources_then_database_sources_in_call_order()
    {
        ServiceCollection services = new();
        services.AddSingleton(NpgsqlDataSource.Create(Cs));
        FakeDatabaseSource tenants = new("acme", "globex");

        PostgresStudioOptions options = new PostgresStudioOptions()
            .AddDatabaseSource("tenants", _ => tenants)
            .AddDataSource("reporting", _ => NpgsqlDataSource.Create(Cs))
            .AddDataSource("admin", _ => NpgsqlDataSource.Create(Cs));

        await using Harness harness = new(services, options);

        harness.Provider.Registrations().Should().Equal(
            new StudioSourceRegistration("default", "Default", StudioSourceKind.DataSource),
            new StudioSourceRegistration("reporting", "reporting", StudioSourceKind.DataSource),
            new StudioSourceRegistration("admin", "admin", StudioSourceKind.DataSource),
            new StudioSourceRegistration("tenants", "tenants", StudioSourceKind.HostProvider));
    }

    [Fact]
    public async Task A_host_source_lists_names_opens_one_when_resolved_and_never_keeps_it()
    {
        ServiceCollection services = new();
        await using NpgsqlDataSource acme = NpgsqlDataSource.Create("Host=acme.example;Database=acme;Username=app");
        FakeDatabaseSource tenants = new("acme", "globex") { DataSources = { ["acme"] = acme } };

        await using Harness harness = new(services, new PostgresStudioOptions().AddDatabaseSource("tenants", _ => tenants));

        IStudioSource source = harness.Resolve("tenants");
        source.Cardinality.Should().Be(StudioDatabaseCardinality.DynamicMultiple);
        source.ShowsTenantSelector.Should().BeFalse();

        IReadOnlyList<IStudioDatabase> listed = await source.DatabasesAsync(Token);
        listed.Select(static x => x.Identity).Should().Equal("acme", "globex");
        listed.Should().OnlyContain(static x => x.DataSource == null, "listing builds no pool");
        tenants.Opened.Should().BeEmpty();

        ResolvedSource resolved = await source.CompleteAsync(new StudioScope("tenants", "acme", null), listed[0], listed, Token);
        resolved.Target.Identity.Should().Be("acme", "a host-listed database's identity is the host's name");
        resolved.Target.DataSource.Should().BeSameAs(acme);
        resolved.Target.Name.Should().Be("acme");

        await source.CompleteAsync(new StudioScope("tenants", "acme", null), listed[0], listed, Token);
        tenants.Opened.Should().Equal("acme", "acme");

        Func<Task> gone = async () => await source.CompleteAsync(new StudioScope("tenants", "globex", null), listed[1], listed, Token);
        await gone.Should().ThrowAsync<KeyNotFoundException>("the host no longer has a data source for it");

        Func<Task> tenant = async () => await source.CompleteAsync(new StudioScope("tenants", "acme", "t1"), listed[0], listed, Token);
        await tenant.Should().ThrowAsync<KeyNotFoundException>("a plain source has no tenants");
    }

    /// <summary>
    /// The review's live finding, as a unit: a policy that allows the empty database id - which the studio asks only to
    /// list a source that would not build - and refuses <c>globex</c> by name must never be answered with globex. The
    /// shared resolver matches an empty id to the first listed database; the source refuses to complete such a scope.
    /// </summary>
    [Fact]
    public async Task A_scope_with_no_database_is_refused_whatever_the_policy_said_about_an_empty_id()
    {
        ServiceCollection services = new();
        services.AddSingleton(NpgsqlDataSource.Create(Cs));
        await using NpgsqlDataSource globex = NpgsqlDataSource.Create("Host=globex.example;Database=globex;Username=app");
        await using NpgsqlDataSource acme = NpgsqlDataSource.Create("Host=acme.example;Database=acme;Username=app");
        FakeDatabaseSource tenants = new("globex", "acme") { DataSources = { ["globex"] = globex, ["acme"] = acme } };

        PostgresStudioOptions options = new PostgresStudioOptions { SourceAuthorizationPolicy = "src" }
            .AddDatabaseSource("tenants", _ => tenants);
        await using Harness harness = new(services, options);

        TestSourceAuthorizationService policy = new();
        policy.Allow(static x => x.DatabaseIdentifier is "" or "acme" or "default");
        StudioSourceResolver resolver = Resolver(harness, options, policy);

        Func<Task> empty = async () => await resolver.ResolveAsync(new StudioScope("tenants", string.Empty, null), cancellationToken: Token);
        await empty.Should().ThrowAsync<KeyNotFoundException>("an empty id is never 'the first database'");

        Func<Task> refused = async () => await resolver.ResolveAsync(new StudioScope("tenants", "globex", null), cancellationToken: Token);
        await refused.Should().ThrowAsync<StudioNotAuthorizedException>();

        (await resolver.ResolveAsync(new StudioScope("tenants", "acme", null), cancellationToken: Token)).Target.DataSource.Should().BeSameAs(acme);

        Func<Task> single = async () => await resolver.ResolveAsync(new StudioScope("default", string.Empty, null), cancellationToken: Token);
        await single.Should().ThrowAsync<KeyNotFoundException>("a data source's one database is named by its key, never by an empty id");

        tenants.Opened.Should().Equal(["acme"], "nothing was opened for the empty id or for globex");
    }

    [Fact]
    public async Task The_resolver_matches_a_listed_name_and_refuses_an_unlisted_one()
    {
        ServiceCollection services = new();
        await using NpgsqlDataSource acme = NpgsqlDataSource.Create("Host=acme.example;Database=acme;Username=app");
        FakeDatabaseSource tenants = new("acme") { DataSources = { ["acme"] = acme } };

        await using Harness harness = new(services, new PostgresStudioOptions().AddDatabaseSource("tenants", _ => tenants));

        IStudioSource source = harness.Resolve("tenants");
        IReadOnlyList<IStudioDatabase> listed = await source.DatabasesAsync(Token);

        StudioSourceResolver.Match(listed, "ACME")!.Identity.Should().Be("acme");
        StudioSourceResolver.Match(listed, "initech").Should().BeNull("a name the host never listed is never opened");
        tenants.Opened.Should().BeEmpty();
    }

    [Fact]
    public async Task A_host_list_leaves_out_invalid_duplicate_and_another_sources_names()
    {
        ServiceCollection services = new();
        services.AddSingleton(NpgsqlDataSource.Create(Cs));
        FakeDatabaseSource tenants = new("acme", "ACME", "has space", "a/b", "", "-leading", "admin", "default", "globex");

        PostgresStudioOptions options = new PostgresStudioOptions()
            .AddDataSource("admin", _ => NpgsqlDataSource.Create(Cs))
            .AddDatabaseSource("tenants", _ => tenants);

        await using Harness harness = new(services, options);
        harness.Provider.Registrations();

        IReadOnlyList<IStudioDatabase> listed = await harness.Resolve("tenants").DatabasesAsync(Token);

        listed.Select(static x => x.Identity).Should().Equal(["acme", "globex"],
            "a duplicate ignoring case, an invalid name and a name another source already answers to are left out");
    }

    /// <summary>
    /// A name that ends in a line break is left out as any invalid name is, counted under the same reason - never kept as
    /// an identity of its own, which would reach the resource a policy is asked about, the URL, the audit and the logs.
    /// </summary>
    [Fact]
    public async Task A_host_listed_name_ending_in_a_line_break_is_left_out_as_invalid()
    {
        CapturingLoggerProvider logs = new();
        using ILoggerFactory factory = logs.CreateFactory();
        ServiceCollection services = new();
        FakeDatabaseSource tenants = new("acme", "tenant_a\n", "tenant_a\r\n", "tenant_a\u2028", "globex");

        await using Harness harness = new(
            services,
            new PostgresStudioOptions().AddDatabaseSource("tenants", _ => tenants),
            logger: factory.CreateLogger<PostgresStudioSourceProvider>());

        IReadOnlyList<IStudioDatabase> listed = await harness.Resolve("tenants").DatabasesAsync(Token);

        listed.Select(static x => x.Identity).Should().Equal(["acme", "globex"]);
        logs.Entries.Should().ContainSingle(static x => x.EventId.Id == 9301)
            .Which.Message.Should().Be(
                "Postgres Studio left 3 of the databases source tenants listed out: they do not match ^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$");
    }

    [Fact]
    public async Task A_host_list_is_kept_for_sixty_seconds_then_read_again()
    {
        ServiceCollection services = new();
        FakeDatabaseSource tenants = new("acme");
        ManualTimeProvider time = new();

        await using Harness harness = new(services, new PostgresStudioOptions().AddDatabaseSource("tenants", _ => tenants), time);

        IStudioSource source = harness.Resolve("tenants");
        await source.DatabasesAsync(Token);
        tenants.Names.Add("globex");

        time.Advance(TimeSpan.FromSeconds(59));
        (await source.DatabasesAsync(Token)).Select(static x => x.Identity).Should().Equal(["acme"], "a list is kept for a minute");
        tenants.Lists.Should().Be(1);

        time.Advance(TimeSpan.FromSeconds(2));
        (await source.DatabasesAsync(Token)).Select(static x => x.Identity).Should().Equal("acme", "globex");
        tenants.Lists.Should().Be(2);
    }

    [Fact]
    public async Task A_refresh_reads_a_host_list_again_at_once()
    {
        ServiceCollection services = new();
        FakeDatabaseSource tenants = new("acme");

        await using Harness harness = new(services, new PostgresStudioOptions().AddDatabaseSource("tenants", _ => tenants), new ManualTimeProvider());

        IStudioSource source = harness.Resolve("tenants");
        await source.DatabasesAsync(Token);
        await source.DatabasesAsync(Token);
        tenants.Lists.Should().Be(1);

        long before = harness.Provider.Generation;
        harness.Provider.Invalidate();
        harness.Provider.Generation.Should().Be(before + 1);

        tenants.Names.Add("globex");
        (await source.DatabasesAsync(Token)).Select(static x => x.Identity).Should().Equal("acme", "globex");
        tenants.Lists.Should().Be(2);
    }

    [Fact]
    public async Task A_host_list_that_throws_is_a_failure_and_is_asked_again()
    {
        ServiceCollection services = new();
        FakeDatabaseSource tenants = new("acme") { ListFailure = new InvalidOperationException("admin database down") };

        await using Harness harness = new(services, new PostgresStudioOptions().AddDatabaseSource("tenants", _ => tenants));

        IStudioSource source = harness.Resolve("tenants");
        Func<Task> list = async () => await source.DatabasesAsync(Token);
        await list.Should().ThrowAsync<InvalidOperationException>().WithMessage("admin database down");

        tenants.ListFailure = null;
        (await source.DatabasesAsync(Token)).Should().ContainSingle();
    }

    [Fact]
    public async Task A_host_list_that_takes_longer_than_QueryTimeout_is_a_timeout_not_a_cancellation()
    {
        ServiceCollection services = new();
        FakeDatabaseSource tenants = new("acme") { HangListing = true };
        ManualTimeProvider time = new();

        await using Harness harness = new(
            services,
            new PostgresStudioOptions { QueryTimeout = TimeSpan.FromSeconds(5) }.AddDatabaseSource("tenants", _ => tenants),
            time);

        IStudioSource source = harness.Resolve("tenants");
        Task<IReadOnlyList<IStudioDatabase>> listing = source.DatabasesAsync(Token).AsTask();

        await tenants.ListingStarted.Task.WaitAsync(TimeSpan.FromSeconds(30), Token);
        time.Advance(TimeSpan.FromSeconds(6));

        Func<Task> wait = async () => await listing;
        (await wait.Should().ThrowAsync<TimeoutException>()).WithMessage("*did not list its databases within 5 s*");
    }

    [Fact]
    public async Task Opening_a_database_that_takes_longer_than_QueryTimeout_is_a_timeout()
    {
        ServiceCollection services = new();
        FakeDatabaseSource tenants = new("acme") { HangOpening = true };
        ManualTimeProvider time = new();

        await using Harness harness = new(
            services,
            new PostgresStudioOptions { QueryTimeout = TimeSpan.FromSeconds(5) }.AddDatabaseSource("tenants", _ => tenants),
            time);

        IStudioSource source = harness.Resolve("tenants");
        IReadOnlyList<IStudioDatabase> listed = await source.DatabasesAsync(Token);
        Task<ResolvedSource> opening = source.CompleteAsync(new StudioScope("tenants", "acme", null), listed[0], listed, Token).AsTask();

        await tenants.OpeningStarted.Task.WaitAsync(TimeSpan.FromSeconds(30), Token);
        time.Advance(TimeSpan.FromSeconds(6));

        Func<Task> wait = async () => await opening;
        (await wait.Should().ThrowAsync<StudioStoreUnavailableException>()).WithMessage("*did not hand over database 'acme' within 5 s*");
    }

    [Fact]
    public async Task A_host_list_is_read_to_ten_thousand_names_and_says_it_left_the_rest_out()
    {
        CapturingLoggerProvider logs = new();
        using ILoggerFactory factory = logs.CreateFactory();
        ServiceCollection services = new();
        FakeDatabaseSource tenants = new([.. Enumerable.Range(0, PostgresSourceRegistry.MaxListedDatabases + 5).Select(static x => "t" + x)]);

        await using Harness harness = new(
            services,
            new PostgresStudioOptions().AddDatabaseSource("tenants", _ => tenants),
            logger: factory.CreateLogger<PostgresStudioSourceProvider>());

        IReadOnlyList<IStudioDatabase> listed = await harness.Resolve("tenants").DatabasesAsync(Token);

        listed.Should().HaveCount(PostgresSourceRegistry.MaxListedDatabases);
        listed[^1].Identity.Should().Be("t9999");
        logs.Entries.Should().ContainSingle(static x => x.EventId.Id == 9301)
            .Which.Message.Should().Contain("source tenants").And.Contain("more than 10000 databases").And.NotContain("t10000");
    }

    [Fact]
    public async Task A_factory_that_throws_is_unavailable_and_remembered()
    {
        ServiceCollection services = new();
        int calls = 0;
        PostgresStudioOptions options = new PostgresStudioOptions().AddDataSource("broken", _ =>
        {
            calls++;
            throw new InvalidOperationException("no such data source");
        });

        await using Harness harness = new(services, options);
        StudioSourceRegistration broken = harness.Provider.Find("broken")!;

        StoreAvailability first = harness.Provider.TryResolve(broken, out IStudioSource? source);
        StoreAvailability second = harness.Provider.TryResolve(broken, out _);

        first.IsAvailable.Should().BeFalse();
        first.Message.Should().Be("no such data source");
        source.Should().BeNull();
        second.Should().BeSameAs(first, "a failure is answered from memory for ten seconds");
        calls.Should().Be(1);
    }

    [Fact]
    public async Task The_studio_never_disposes_a_data_source_it_did_not_create()
    {
        ServiceCollection services = new();
        await using NpgsqlDataSource own = NpgsqlDataSource.Create(Cs);

        await using (Harness harness = new(services, new PostgresStudioOptions().AddDataSource("again", _ => own)))
        {
            foreach (StudioSourceRegistration registration in harness.Provider.Registrations())
            {
                harness.Provider.TryResolve(registration, out _).IsAvailable.Should().BeTrue();
            }
        }

        // Still usable: neither the provider nor the registry disposed the application's instance.
        Action create = () => own.CreateConnection().Dispose();
        create.Should().NotThrow();
    }

    [Fact]
    public async Task A_sweep_covers_every_database_opening_each_host_database_as_it_is_reached()
    {
        ServiceCollection services = new();
        services.AddSingleton(NpgsqlDataSource.Create(Cs));
        await using NpgsqlDataSource acme = NpgsqlDataSource.Create("Host=acme.example;Database=acme;Username=app");
        FakeDatabaseSource tenants = new("acme", "gone", "broken")
        {
            DataSources = { ["acme"] = acme },
            OpenFailures = { ["broken"] = new InvalidOperationException("tenant database down") },
        };

        await using Harness harness = new(services, new PostgresStudioOptions().AddDatabaseSource("tenants", _ => tenants));

        List<StudioSweepTarget> targets = [];
        await foreach (StudioSweepTarget target in harness.Provider.SweepTargetsAsync(Token))
        {
            targets.Add(target);
        }

        targets.Select(static x => (x.Source.Key, x.Outcome, x.Resolved?.Scope.DatabaseId)).Should().Equal(
            ("default", StudioSweepTargetOutcome.Database, "default"),
            ("tenants", StudioSweepTargetOutcome.Database, "acme"),
            ("tenants", StudioSweepTargetOutcome.SourceNotReached, null));
        targets.Should().OnlyContain(static x => x.Resolved == null || x.Resolved.TenantId == null);
    }

    [Fact]
    public void The_descriptor_builds_the_tenantless_public_resource()
    {
        object resource = PostgresStudioDescriptor.Instance.CreateResource(new StudioScope("tenants", "acme", null), "RunSql");

        resource.Should().Be(new PostgresStudioResource("tenants", "acme", "RunSql"));
    }

    /// <summary>The shared resolver over the harness's provider, a test policy and no signed-in visitor.</summary>
    private static StudioSourceResolver Resolver(Harness harness, PostgresStudioOptions options, IAuthorizationService policy)
    {
        IStudioSettings settings = PostgresStudioSettings.Of(options);
        StudioAuthorization authorization = new(settings, PostgresStudioDescriptor.Instance, policy, new TestAuthenticationStateProvider());
        return new StudioSourceResolver(harness.Provider, authorization, PostgresStudioDescriptor.Instance);
    }

    /// <summary>A service the root provider must refuse (scope validation is on in the harness).</summary>
    private sealed class ScopedProbe;

    /// <summary>A host's database source, as a test says it answers.</summary>
    private sealed class FakeDatabaseSource : IPostgresDatabaseSource
    {
        public FakeDatabaseSource(params string[] names) => Names = [.. names];

        public List<string> Names { get; }

        public Dictionary<string, NpgsqlDataSource> DataSources { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, Exception> OpenFailures { get; } = new(StringComparer.Ordinal);

        public Exception? ListFailure { get; set; }

        public bool HangListing { get; init; }

        public bool HangOpening { get; init; }

        public TaskCompletionSource ListingStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource OpeningStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Lists { get; private set; }

        public List<string> Opened { get; } = [];

        public async IAsyncEnumerable<string> ListAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Lists++;
            ListingStarted.TrySetResult();

            if (HangListing)
            {
                // Waits on the token alone: nothing but the studio's own timeout ends it.
                await new TaskCompletionSource().Task.WaitAsync(cancellationToken);
            }

            await Task.Yield();

            if (ListFailure is { } failure)
            {
                throw failure;
            }

            foreach (string name in Names.ToArray())
            {
                yield return name;
            }
        }

        public async ValueTask<NpgsqlDataSource?> GetDataSourceAsync(string name, CancellationToken cancellationToken)
        {
            Opened.Add(name);
            OpeningStarted.TrySetResult();

            if (HangOpening)
            {
                await new TaskCompletionSource().Task.WaitAsync(cancellationToken);
            }

            if (OpenFailures.TryGetValue(name, out Exception? failure))
            {
                throw failure;
            }

            return DataSources.GetValueOrDefault(name);
        }
    }
}
