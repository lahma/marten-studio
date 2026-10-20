using Marten;

using MartenStudio.Services;
using MartenStudio.Services.Settings;
using MartenStudio.Services.Sources;
using MartenStudio.Tests.Support;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MartenStudio.Tests.Services;

/// <summary>
/// The seams PS-1a put between the shared studio and MartenStudio: the settings adapter, the descriptor, the source
/// provider, the registration marker and the pass-throughs - each answering exactly what the code it replaced did.
/// </summary>
public class StudioSpineTests
{
    /// <summary>Never connected to: only descriptors and configuration are read.</summary>
    private const string DummyConnectionString =
        "Host=marten-studio-spine-test.invalid;Database=spine;Username=none;Password=none";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // ---------------------------------------------------------------------------------------------
    // Settings
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void The_settings_read_the_options_live_so_a_path_written_after_startup_is_seen()
    {
        MartenStudioOptions options = new();
        MartenStudioSettings settings = MartenSpine.Settings(options);

        settings.TrimmedPath.Should().Be("/marten");
        settings.HasCustomPath.Should().BeFalse();

        // What MapMartenStudio(pattern) does to the resolved instance.
        options.Path = "/ops/my studio/";

        settings.TrimmedPath.Should().Be("/ops/my studio");
        settings.EscapedPath.Should().Be("/ops/my%20studio");
        settings.HasCustomPath.Should().BeTrue();
    }

    [Fact]
    public void The_settings_spell_every_option_the_way_a_host_registration_does()
    {
        IStudioSettings settings = MartenSpine.Settings();

        settings.ProductName.Should().Be("Marten Studio");
        settings.OptionName("ReadOnly").Should().Be("MartenStudioOptions.ReadOnly");
        settings.CapabilityOption(StudioCapability.RunSql).Should().Be("MartenStudioOptions.Capabilities.RunSql");
        settings.ReadOnlyOption().Should().Be("MartenStudioOptions.ReadOnly");
        settings.AllCapabilities().Should().Be("MartenStudioCapabilities.All()");
    }

    [Fact]
    public void The_settings_map_every_capability_and_offer_all_thirteen()
    {
        MartenStudioOptions options = new() { Capabilities = new MartenStudioCapabilities { BrowseDatabase = true } };
        MartenStudioSettings settings = MartenSpine.Settings(options);

        settings.Capabilities.Should().Equal(StudioCapabilityGuard.All);
        foreach (StudioCapability capability in StudioCapabilityGuard.All)
        {
            settings.IsConfigured(capability).Should().Be(capability == StudioCapability.BrowseDatabase, "{0}", capability);
        }

        Action unknown = () => settings.IsConfigured((StudioCapability) 999);
        unknown.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void The_settings_carry_the_policies_and_the_limits_unchanged()
    {
        MartenStudioOptions options = new()
        {
            StoreAuthorizationPolicy = "store",
            WriteAuthorizationPolicy = "write",
            AuthorizationPolicy = "endpoint",
            MaxInlineDocumentBytes = 1234,
            SqlConsoleRole = "studio_reader",
            IsDocumentTypeVisible = static _ => false,
        };
        options.BrowsableSchemas.Add("legacy");

        MartenStudioSettings settings = MartenSpine.Settings(options);

        settings.ScopePolicy.Should().Be("store");
        settings.WritePolicy.Should().Be("write");
        settings.AuthorizationPolicy.Should().Be("endpoint");
        settings.MaxInlineValueBytes.Should().Be(1234);
        settings.SqlConsoleRole.Should().Be("studio_reader");
        settings.BrowsableSchemas.Should().Equal("legacy");
        settings.IsDocumentTypeVisible.Should().BeSameAs(options.IsDocumentTypeVisible);
        settings.EffectiveHealthInterval.Should().Be(options.HealthInterval);
    }

    // ---------------------------------------------------------------------------------------------
    // Descriptor
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void The_descriptor_builds_the_resource_a_policy_has_always_been_shown()
    {
        StudioScope scope = new("default", "server.database", "acme");

        MartenSpine.Descriptor.CreateResource(scope, null).Should().Be(new MartenStoreResource("default", "server.database", "acme", null));
        MartenSpine.Descriptor.CreateResource(scope, "EditDocuments")
            .Should().Be(new MartenStoreResource("default", "server.database", "acme", "EditDocuments"));
    }

    [Fact]
    public void The_descriptor_carries_the_sidebar_and_its_conditions()
    {
        StudioDescriptor descriptor = MartenSpine.Descriptor;

        descriptor.SourceQueryParameter.Should().Be("store");
        descriptor.Navigation.SelectMany(static x => x.Items).Select(static x => x.Label).Should().Equal(
            "Overview", "Documents", "Query", "Tables", "Relationships", "Streams", "Feed", "Event types", "Dead letters",
            "Health", "Projections", "Schema", "Configuration", "Activity");

        StudioNavItem deadLetters = descriptor.Navigation.SelectMany(static x => x.Items).Single(static x => x.Label == "Dead letters");
        deadLetters.IsShown.Should().NotBeNull();
        deadLetters.IsShown!(MartenSpine.Settings()).Should().BeTrue();
        deadLetters.IsShown(MartenSpine.Settings(new MartenStudioOptions { IsDocumentTypeVisible = static _ => false }))
            .Should().BeFalse("a hidden DeadLetterEvent takes its entry with it, failing closed (D27)");
    }

    // ---------------------------------------------------------------------------------------------
    // Registration
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Registering_the_same_studio_twice_is_allowed_and_a_second_studio_is_refused()
    {
        ServiceCollection services = new();
        services.AddMartenStudio();
        services.AddMartenStudio();

        services.Count(static x => x.ServiceType == typeof(StudioRegistrationMarker)).Should().Be(1);

        Action other = () => StudioRegistrationMarker.Claim(services, new StudioRegistrationMarker("postgre", "Postgres Studio", "AddPostgresStudio()"));

        other.Should().Throw<InvalidOperationException>()
            .WithMessage("AddPostgresStudio() was called in an application that already registered Marten Studio with AddMartenStudio()*");
    }

    [Fact]
    public async Task AddMartenStudio_registers_every_seam_in_Marten_s_terms()
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddMarten(x => x.Connection(DummyConnectionString));
        services.AddMartenStudio();

        await using ServiceProvider provider = services.BuildServiceProvider();
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        IServiceProvider scoped = scope.ServiceProvider;

        scoped.GetRequiredService<IStudioSettings>().Should().BeOfType<MartenStudioSettings>();
        scoped.GetRequiredService<StudioDescriptor>().Should().BeSameAs(MartenStudioDescriptor.Instance);
        scoped.GetRequiredService<IStudioLinks>().Should().BeOfType<MartenStudioLinks>();
        scoped.GetRequiredService<IStudioSourceProvider>().Should().BeOfType<MartenStudioSourceProvider>();
        scoped.GetRequiredService<ISessionLockLabeler>().Should().BeOfType<MartenSessionLockLabeler>();
        scoped.GetRequiredService<IHealthTableSource>().Should().BeOfType<MartenHealthTableSource>();
        scoped.GetRequiredService<IHealthRemedyProvider>().Should().BeOfType<MartenHealthRemedyProvider>();
        services.Where(static x => x.ServiceType == typeof(INavIndicatorContributor))
            .Should().ContainSingle().Which.ImplementationType.Should().Be<MartenNavIndicatorContributor>();
        services.Should().Contain(static x => x.ServiceType == typeof(MartenStudio.Services.Query.SqlConsoleService)
            && x.ImplementationType == typeof(MartenStudio.Services.Query.SqlConsoleService));
        services.Should().ContainSingle(static x => x.ServiceType == typeof(ISqlConsoleService))
            .Which.ImplementationFactory.Should().NotBeNull("the interface forwards to the one scoped console the Query service also uses");
        services.Should().Contain(static x => x.ServiceType == typeof(IRelationshipNodeSource) && x.ImplementationType == typeof(MartenRelationshipNodeSource));
        services.Should().Contain(static x => x.ServiceType == typeof(IDatabaseDeclarationsSource) && x.ImplementationType == typeof(MartenDatabaseDeclarationsSource));
    }

    // ---------------------------------------------------------------------------------------------
    // Sources
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_source_provider_answers_the_store_its_databases_and_their_identity()
    {
        await using SourceHarness harness = SourceHarness.Create();

        harness.Sources.Registrations().Should().Equal(new StudioSourceRegistration("default", "Default", StudioSourceKind.MartenStore));
        harness.Sources.Find("DEFAULT").Should().Be(harness.Sources.Registrations()[0], "keys compare ignoring case");
        harness.Sources.Find("nope").Should().BeNull();

        harness.Sources.TryResolve(harness.Sources.Registrations()[0], out IStudioSource? source).IsAvailable.Should().BeTrue();
        source!.Cardinality.Should().Be(StudioDatabaseCardinality.Single);
        source.ShowsTenantSelector.Should().BeFalse();

        IReadOnlyList<IStudioDatabase> databases = await source.DatabasesAsync(Token);
        global::Marten.Storage.IMartenDatabase marten = (await harness.Store.Storage.AllDatabases())[0];

        databases.Should().ContainSingle();
        databases[0].Identity.Should().Be(marten.Id.Identity);
        databases[0].Name.Should().Be("spine");
        databases[0].DataSource.Should().NotBeNull("Marten 9's database is a Weasel PostgresqlDatabase with a data source");
        MartenStudioDatabase.Unwrap(databases[0]).Should().BeSameAs(marten);
    }

    [Fact]
    public async Task Resolving_through_the_shared_resolver_answers_Marten_s_resolved_scope()
    {
        await using SourceHarness harness = SourceHarness.Create();

        ResolvedScope resolved = await harness.Resolver.ResolveAsync(new StudioScope("default", string.Empty, null), null, Token);

        resolved.Store.Should().BeSameAs(harness.Store);
        resolved.Source.Should().Be(new StudioSourceRegistration("default", "Default", StudioSourceKind.MartenStore));
        resolved.Target.Identity.Should().Be(resolved.Database.Id.Identity);
        MartenStudioDatabase.Unwrap(resolved.Target).Should().BeSameAs(resolved.Database);
        resolved.Should().Be(new ResolvedScope(resolved.Scope, resolved.Registration, resolved.Store, resolved.Database), "a wrapper of one instance is equal to another of it");
    }

    /// <summary>
    /// The shared view and Marten's cannot be split by a copy: the members the base record is built from, and the base
    /// record's own, are get-only, so no <c>with</c> can replace one without the other; <c>with { Scope = … }</c> still works.
    /// </summary>
    [Fact]
    public async Task A_copy_cannot_split_the_shared_view_from_Marten_s()
    {
        await using SourceHarness harness = SourceHarness.Create();
        ResolvedScope resolved = await harness.Resolver.ResolveAsync(new StudioScope("default", string.Empty, null), null, Token);

        foreach ((Type type, string property) in new[]
                 {
                     (typeof(ResolvedSource), nameof(ResolvedSource.Source)),
                     (typeof(ResolvedSource), nameof(ResolvedSource.Target)),
                     (typeof(ResolvedScope), nameof(ResolvedScope.Registration)),
                     (typeof(ResolvedScope), nameof(ResolvedScope.Database)),
                 })
        {
            type.GetProperty(property)!.SetMethod.Should().BeNull("{0}.{1} is get-only", type.Name, property);
        }

        ResolvedScope moved = resolved with { Scope = resolved.Scope with { TenantId = "acme" } };

        moved.TenantId.Should().Be("acme");
        moved.Target.Should().Be(resolved.Target);
        moved.Database.Should().BeSameAs(resolved.Database);
    }

    [Fact]
    public async Task A_tenant_of_a_store_without_tenancy_is_not_found()
    {
        await using SourceHarness harness = SourceHarness.Create();

        await harness.Resolver
            .Invoking(x => x.ResolveAsync(new StudioScope("default", string.Empty, "acme"), null, Token).AsTask())
            .Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task A_sweep_with_no_visitor_covers_every_database_the_store_holds()
    {
        await using SourceHarness harness = SourceHarness.Create();

        List<StudioSweepTarget> targets = [];
        await foreach (StudioSweepTarget target in harness.Sources.SweepTargetsAsync(Token))
        {
            targets.Add(target);
        }

        StudioSweepTarget only = targets.Should().ContainSingle().Subject;
        only.Outcome.Should().Be(StudioSweepTargetOutcome.Database);
        only.Resolved.Should().BeOfType<ResolvedScope>().Which.TenantId.Should().BeNull();
        only.Resolved!.Scope.DatabaseId.Should().Be(only.Resolved.Target.Identity);
    }

    [Fact]
    public async Task A_store_that_will_not_build_is_a_sweep_target_not_reached()
    {
        ServiceCollection services = new();
        services.AddSingleton<MartenStoreRegistryTest.IBrokenStore>(static _ => throw new InvalidOperationException("broken"));
        await using ServiceProvider provider = services.BuildServiceProvider();

        IOptions<MartenStudioOptions> options = Options.Create(new MartenStudioOptions());
        MartenStudioSourceProvider sources = new(
            options,
            new MartenStoreRegistry(services),
            new TenantDiscovery(options, NullLogger<TenantDiscovery>.Instance),
            provider,
            NullLogger<MartenStudioSourceProvider>.Instance);

        List<StudioSweepTarget> targets = [];
        await foreach (StudioSweepTarget target in sources.SweepTargetsAsync(Token))
        {
            targets.Add(target);
        }

        targets.Should().ContainSingle().Which.Outcome.Should().Be(StudioSweepTargetOutcome.SourceNotReached);
    }

    // ---------------------------------------------------------------------------------------------
    // The pass-throughs' pure parts
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("qrtz_triggers", "Quartz.NET")]
    [InlineData("wolverine_incoming_envelopes", "Wolverine")]
    [InlineData("mt_events", "Marten")]
    [InlineData("mt_hilo", "Marten")]
    [InlineData("mt_doc_order", null)]
    [InlineData("__EFMigrationsHistory", "Entity Framework Core")]
    [InlineData("VersionInfo", "FluentMigrator")]
    [InlineData("orders", null)]
    public void A_library_s_table_is_recognised_by_name(string table, string? library)
    {
        StudioTableLibraries.LibraryOf(table).Should().Be(library);
    }

    [Fact]
    public void The_sidebar_badges_read_as_they_always_have()
    {
        IReadOnlyDictionary<string, StudioNavBadge> badges = MartenNavBadges.From(1234, projectionsNeedAttention: true, "OrderSummary:All is 5,000 events behind");

        badges[MartenStudioDescriptor.DeadLettersBadge].Should().Be(
            new StudioNavBadge("1,234", "ms-nav-badge-danger", $"{1234:N0} events the async daemon could not apply"));
        badges[MartenStudioDescriptor.ProjectionsBadge].Should().Be(
            new StudioNavBadge(string.Empty, "ms-nav-badge-dot ms-nav-badge-warning", "OrderSummary:All is 5,000 events behind"));

        MartenNavBadges.From(1, false, null)[MartenStudioDescriptor.DeadLettersBadge].Title.Should().Be("One event the async daemon could not apply");
        MartenNavBadges.From(0, false, null).Should().BeEmpty("zero draws no badge");
        MartenNavBadges.From(null, false, null).Should().BeEmpty("could not tell draws no badge");
    }

    [Fact]
    public void A_storage_parameter_remedy_is_a_StoreOptions_line_for_a_document_table_and_nothing_otherwise()
    {
        MartenHealthRemedyProvider remedies = new();
        StudioTable document = new("public", "mt_doc_order", StudioTableKind.DocumentTable, StudioTableOwnership.Owned) { TypeName = "Order" };
        StudioTable events = new("public", "mt_events", StudioTableKind.EventTable, StudioTableOwnership.Library) { Library = "Marten" };

        remedies.StorageParameter(document, "fillfactor", "90", "Leaves room for HOT updates.")!.Code
            .Should().Be("opts.Schema.For<Order>().StorageParameter(\"fillfactor\", 90);");
        remedies.StorageParameter(events, "fillfactor", "90", "x").Should().BeNull();
        events.ExemptFromIndexDesign.Should().BeTrue("a library's table is exempt from the index-design rules only");
        document.ExemptFromIndexDesign.Should().BeFalse();
    }

    /// <summary>A Marten store over a connection string nothing connects to, and MartenStudio's sources over it.</summary>
    private sealed class SourceHarness : IAsyncDisposable
    {
        private readonly ServiceProvider provider;

        private SourceHarness(ServiceProvider provider, MartenStudioSourceProvider sources, StudioScopeResolver resolver)
        {
            this.provider = provider;
            Sources = sources;
            Resolver = resolver;
        }

        public MartenStudioSourceProvider Sources { get; }

        public StudioScopeResolver Resolver { get; }

        public IDocumentStore Store => provider.GetRequiredService<IDocumentStore>();

        public static SourceHarness Create()
        {
            ServiceCollection services = new();
            services.AddMarten(x => x.Connection(DummyConnectionString));
            ServiceProvider provider = services.BuildServiceProvider();

            IOptions<MartenStudioOptions> options = Options.Create(new MartenStudioOptions());
            StudioAuthorization authorization = MartenSpine.Authorization(options, new TestStoreAuthorizationService(), new TestAuthenticationStateProvider());
            var (sources, _, resolver) = MartenSpine.Scope(
                options,
                new MartenStoreRegistry(services),
                authorization,
                new TenantDiscovery(options, NullLogger<TenantDiscovery>.Instance),
                provider);

            return new SourceHarness(provider, sources, resolver);
        }

        public ValueTask DisposeAsync() => provider.DisposeAsync();
    }
}
