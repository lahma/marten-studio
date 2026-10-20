using System.Data.Common;
using System.Runtime.CompilerServices;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Npgsql;

using PostgresStudio.Services;
using PostgresStudio.Services.Postgres;
using PostgresStudio.Services.Sources;

namespace PostgresStudio.Tests.Sources;

/// <summary>
/// PostgresStudio's sources over DI descriptors: the unkeyed singleton data source is <c>default</c>, a keyed one only
/// through <c>AddDataSource</c>, a host's database source after them; what each is named, how many databases it has,
/// that nothing is disposed, and what a host's list leaves out.
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
        public Harness(IServiceCollection services, PostgresStudioOptions options)
        {
            Options = options;
            Registry = new PostgresSourceRegistry(services);
            services.AddSingleton(Registry);
            Root = services.BuildServiceProvider();
            Scope = Root.CreateAsyncScope();
            Provider = new PostgresStudioSourceProvider(
                Microsoft.Extensions.Options.Options.Create(options),
                Registry,
                Scope.ServiceProvider,
                NullLogger<PostgresStudioSourceProvider>.Instance);
        }

        public PostgresStudioOptions Options { get; }

        public PostgresSourceRegistry Registry { get; }

        public ServiceProvider Root { get; }

        public AsyncServiceScope Scope { get; }

        public PostgresStudioSourceProvider Provider { get; }

        public IStudioSource Resolve(string key)
        {
            StudioSourceRegistration registration = Provider.Find(key) ?? throw new InvalidOperationException("No " + key);
            Provider.TryResolve(registration, out IStudioSource? source).IsAvailable.Should().BeTrue();
            return source!;
        }

        public async ValueTask DisposeAsync()
        {
            await Scope.DisposeAsync();
            await Root.DisposeAsync();
        }
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
    public async Task A_scoped_or_transient_unkeyed_data_source_is_not_a_source()
    {
        ServiceCollection scoped = new();
        scoped.AddScoped<NpgsqlDataSource>(_ => NpgsqlDataSource.Create(Cs));
        await using Harness first = new(scoped, new PostgresStudioOptions());
        first.Provider.Registrations().Should().BeEmpty();

        ServiceCollection transient = new();
        transient.AddTransient<NpgsqlDataSource>(_ => NpgsqlDataSource.Create(Cs));
        await using Harness second = new(transient, new PostgresStudioOptions());
        second.Provider.Registrations().Should().BeEmpty();
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
    public async Task A_keyed_data_source_is_listed_only_through_AddDataSource_and_its_factory_is_called_once()
    {
        ServiceCollection services = new();
        services.AddSingleton(NpgsqlDataSource.Create(Cs));
        AddNpgsqlDataSourceShape(services, "Host=orders.example;Database=orders;Username=app", key: "orders");

        int calls = 0;
        PostgresStudioOptions options = new PostgresStudioOptions().AddDataSource("orders", sp =>
        {
            calls++;
            return sp.GetRequiredKeyedService<NpgsqlDataSource>("orders");
        });

        await using Harness harness = new(services, options);

        harness.Provider.Registrations().Select(static x => x.Key).Should().Equal("default", "orders");

        IStudioSource orders = harness.Resolve("orders");
        IStudioSource again = harness.Resolve("Orders");

        calls.Should().Be(1, "the factory is called once and its answer kept");
        IStudioDatabase database = (await orders.DatabasesAsync(Token)).Should().ContainSingle().Subject;
        database.Identity.Should().Be("orders");
        database.Name.Should().Be("orders");
        database.DataSource.Should().BeSameAs(harness.Root.GetRequiredKeyedService<NpgsqlDataSource>("orders"));
        (await again.DatabasesAsync(Token)).Single().DataSource.Should().BeSameAs(database.DataSource);
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

    [Fact]
    public async Task A_host_list_is_read_once_a_minute_and_again_after_a_refresh()
    {
        ServiceCollection services = new();
        FakeDatabaseSource tenants = new("acme");

        await using Harness harness = new(services, new PostgresStudioOptions().AddDatabaseSource("tenants", _ => tenants));

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
        services.AddSingleton(own);

        await using (Harness harness = new(services, new PostgresStudioOptions().AddDataSource("again", _ => own)))
        {
            foreach (StudioSourceRegistration registration in harness.Provider.Registrations())
            {
                harness.Provider.TryResolve(registration, out _).IsAvailable.Should().BeTrue();
            }
        }

        // Still usable: neither the scope nor the provider disposed the application's instance.
        Action create = () => own.CreateConnection().Dispose();
        create.Should().NotThrow();
    }

    [Fact]
    public async Task A_sweep_covers_every_database_opening_each_host_database_as_it_is_reached()
    {
        ServiceCollection services = new();
        services.AddSingleton(NpgsqlDataSource.Create(Cs));
        await using NpgsqlDataSource acme = NpgsqlDataSource.Create("Host=acme.example;Database=acme;Username=app");
        FakeDatabaseSource tenants = new("acme", "gone") { DataSources = { ["acme"] = acme } };

        await using Harness harness = new(services, new PostgresStudioOptions().AddDatabaseSource("tenants", _ => tenants));

        List<StudioSweepTarget> targets = [];
        await foreach (StudioSweepTarget target in harness.Provider.SweepTargetsAsync(Token))
        {
            targets.Add(target);
        }

        targets.Select(static x => (x.Source.Key, x.Outcome, x.Resolved?.Scope.DatabaseId)).Should().Equal(
            ("default", StudioSweepTargetOutcome.Database, "default"),
            ("tenants", StudioSweepTargetOutcome.Database, "acme"));
        targets.Should().OnlyContain(static x => x.Resolved == null || x.Resolved.TenantId == null);
    }

    [Fact]
    public void The_descriptor_builds_the_tenantless_public_resource()
    {
        object resource = PostgresStudioDescriptor.Instance.CreateResource(new StudioScope("tenants", "acme", null), "RunSql");

        resource.Should().Be(new PostgresStudioResource("tenants", "acme", "RunSql"));
    }

    /// <summary>A host's database source, as a test says it answers.</summary>
    private sealed class FakeDatabaseSource : IPostgresDatabaseSource
    {
        public FakeDatabaseSource(params string[] names) => Names = [.. names];

        public List<string> Names { get; }

        public Dictionary<string, NpgsqlDataSource> DataSources { get; } = new(StringComparer.Ordinal);

        public Exception? ListFailure { get; set; }

        public int Lists { get; private set; }

        public List<string> Opened { get; } = [];

        public async IAsyncEnumerable<string> ListAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Lists++;
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

        public ValueTask<NpgsqlDataSource?> GetDataSourceAsync(string name, CancellationToken cancellationToken)
        {
            Opened.Add(name);
            return ValueTask.FromResult(DataSources.GetValueOrDefault(name));
        }
    }
}
