using Bunit;

using DataStudio.Internal;
using DataStudio.Services.Settings;
using DataStudio.Services;
using DataStudio.Marten.Tests.Health;
using DataStudio.Marten.Tests.Support;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Npgsql;

using DataStudio.Postgres.Services;
using DataStudio.Postgres.Services.Database;
using DataStudio.Postgres.Services.Health;
using DataStudio.Postgres.Services.Live;
using DataStudio.Postgres.Services.Postgres;
using DataStudio.Postgres.Services.Query;
using DataStudio.Postgres.Services.Relationships;
using DataStudio.Postgres.Services.Settings;
using DataStudio.Postgres.Services.Sources;
using DataStudio.Postgres.Tests.Support;

namespace DataStudio.Postgres.Tests.Components;

/// <summary>
/// A bUnit context wired as <c>AddPostgres()</c> wires a circuit: The Postgres module's settings, descriptor, links and
/// source provider - the real ones, over one <see cref="NpgsqlDataSource" /> registered as the application's unkeyed
/// singleton, which nothing here ever opens - and fakes for every data service a page reads through.
/// </summary>
/// <remarks>
/// The scope catalog is the real one over the real provider, so the header, the frame and every page see the scope a
/// host with one data source would see: <c>?store=default&amp;db=default</c>. What a page reads is the fakes'.
/// </remarks>
internal sealed class PostgresComponentContext : BunitContext
{
    /// <summary>A connection string nothing connects to: no test here opens a connection.</summary>
    public const string ConnectionString = "Host=127.0.0.1;Port=1;Database=app;Username=app;Password=secret;Timeout=1";

    public PostgresComponentContext(Action<IServiceCollection>? configure = null)
    {
        Options = new PostgresOptions();
        ShellOptions = new DataStudioOptions();
        DataSource = NpgsqlDataSource.Create(ConnectionString);
        DatabaseObjects = new FakeDatabaseObjectService();
        TableRows = new FakeTableRowService();
        RelationshipData = new FakeRelationshipDataService();
        Health = new FakeHealthService();
        Sessions = new FakeSessions();
        Storage = new FakeStorage();
        SqlConsole = new FakeSqlConsole();
        AuthorizationService = new TestSourceAuthorizationService();
        AuthenticationState = new TestAuthenticationStateProvider();

        JSInterop.Mode = JSRuntimeMode.Loose;

        Services.AddLogging(static builder => builder.SetMinimumLevel(LogLevel.Warning));
        Services.AddSingleton(DataSource);
        Services.AddSingleton<IOptions<PostgresOptions>>(Microsoft.Extensions.Options.Options.Create(Options));
        Services.AddSingleton<IOptions<PostgresDiagnosticsOptions>>(Microsoft.Extensions.Options.Options.Create<PostgresDiagnosticsOptions>(Options));
        Services.AddSingleton<IOptions<DataStudioOptions>>(Microsoft.Extensions.Options.Options.Create(ShellOptions));
        Services.AddSingleton<IShellSettings, ShellSettings>();
        Services.AddSingleton<IStudioSettings>(static provider =>
            new PostgresSettings(provider.GetRequiredService<IOptions<PostgresOptions>>(), provider.GetRequiredService<IShellSettings>()));
        Services.AddSingleton(PostgresDescriptor.Instance);
        Services.AddSingleton<IStudioLinks, PostgresLinks>();
        Services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor());

        // The real source provider and scope catalog over the one data source above.
        Services.AddSingleton(provider => new PostgresSourceRegistry(Services, provider));
        Services.AddSingleton<IStudioSourceProvider, PostgresSourceProvider>();
        Services.AddSingleton<IStudioScopeCatalog, StudioScopeCatalog>();

        Services.AddSingleton<IDatabaseObjectService>(DatabaseObjects);
        Services.AddSingleton<ITableRowService>(TableRows);
        Services.AddSingleton<IRelationshipDataService>(RelationshipData);
        Services.AddSingleton<ISessionActivityService>(Sessions);
        Services.AddSingleton<DataStudio.Postgres.Services.Storage.IStorageService>(Storage);
        Services.AddSingleton<ISqlConsoleService>(SqlConsole);
        PlanFacts = new FakePlanFacts();
        Services.AddSingleton<IPlanFactsService>(PlanFacts);
        Services.AddSingleton<IPostgresServerReader>(new FakeServerReader());
        Aurora = new FakeAurora();
        Services.AddSingleton<DataStudio.Postgres.Services.Aurora.IAuroraService>(Aurora);
        Services.AddSingleton<INavIndicatorService>(new FakeNavIndicators());
        Services.AddSingleton(new DatabaseBrowserState());
        Services.AddSingleton(new SessionPoolObserver("DataStudio.Postgres.Tests." + Guid.NewGuid().ToString("N")));

        Services.AddSingleton(DataStudio.Postgres.Services.Live.DatabaseLiveLogs.For());
        Services.AddScoped<StudioLiveUpdatesFactory>();

        // 1.2-H-A: Mark on Statements, Storage and Maintenance - the circuit's, so the context's one root is the one circuit.
        Services.AddSingleton<SinceMarks>();

        Services.AddSingleton<IHealthService>(Health);
        Services.AddSingleton<IHealthActionService>(new NoHealthActionService());

        // 1.1-NP3: Database > Connection, and the Overview's one line about it.
        Connection = new FakeConnectionInsightService();
        Services.AddSingleton<DataStudio.Postgres.Services.Connection.IConnectionInsightService>(Connection);
        ConnectionCompare = new FakeConnectionCompareService();
        Services.AddSingleton<DataStudio.Postgres.Services.Connection.IConnectionCompareService>(ConnectionCompare);

        Services.AddSingleton<HealthMutes>();
        Services.AddSingleton<HealthAlerts>();

        Services.AddSingleton<IAuthorizationService>(AuthorizationService);
        Services.AddSingleton<AuthenticationStateProvider>(AuthenticationState);
        Services.AddSingleton<StudioAuthorization>();

        // 1.2-SCOPE-3: the header's Find box. No contributor is registered, so the box is not drawn; a test that wants it adds its own.
        Services.AddSingleton<DataStudio.Postgres.Services.Find.FindService>();

        Services.AddSingleton(static provider =>
        {
            StudioPreferences preferences = ActivatorUtilities.CreateInstance<StudioPreferences>(provider);
            preferences.SelectedTimeZoneId = TimeZoneInfo.Utc.Id;
            return preferences;
        });
        Services.AddSingleton<StudioState>();

        Services.AddSingleton<StudioCapabilityGuard>();

        // The shell as AddPostgres() leaves a circuit: the database as the root module, the shell's descriptor composed from the modules
        // registered, and what the module's scope gate answered. The context's one root is the one circuit, so all three are singletons here.
        Services.AddSingleton<IStudioModule>(static provider => new DatabaseStudioModule(
            provider.GetRequiredService<StudioDescriptor>(), provider.GetRequiredService<IStudioSettings>(), provider.GetRequiredService<StudioCapabilityGuard>()));
        Services.AddSingleton(static provider =>
            StudioModuleComposition.Compose(provider.GetServices<IStudioModule>()));
        Services.AddSingleton<DatabaseScopeAccess>();
        Services.AddSingleton<IStudioModuleRail, DatabaseModuleRail>();
        Services.AddSingleton<ToastService>();
        Services.AddSingleton<StudioActionLogService>();
        Services.AddSingleton<StudioActivity>();
        Services.AddSingleton<StudioActionLog>();
        Services.AddSingleton<IStudioAuditScopeGate, DatabaseAuditGate>();

        MappedEndpoints = new StudioMappedEndpoints();
        Services.AddSingleton(MappedEndpoints);

        Services.AddSingleton<ComponentStatePersistenceManager>();
        Services.AddSingleton(static provider =>
            provider.GetRequiredService<ComponentStatePersistenceManager>().State);

        configure?.Invoke(Services);
    }

    public PostgresOptions Options { get; }

    /// <summary>The shell's options: the mount, the title, the entry policy, the application path and the poll interval are its, not the module's.</summary>
    public DataStudioOptions ShellOptions { get; }

    public NpgsqlDataSource DataSource { get; }

    public FakeDatabaseObjectService DatabaseObjects { get; }

    public FakeTableRowService TableRows { get; }

    public FakeRelationshipDataService RelationshipData { get; }

    public FakeHealthService Health { get; }

    /// <summary>Database › Connection and the Overview's connection line (NP3).</summary>
    public FakeConnectionInsightService Connection { get; }

    /// <summary>"Compare a connection string" on that page (1.1-PASTE).</summary>
    public FakeConnectionCompareService ConnectionCompare { get; }

    public FakeSessions Sessions { get; }

    public FakeStorage Storage { get; }

    public FakeSqlConsole SqlConsole { get; }

    /// <summary>The Plan page's facts read (0.10-A).</summary>
    public FakePlanFacts PlanFacts { get; }

    /// <summary>The Server page's Aurora section (W5-AUR).</summary>
    internal FakeAurora Aurora { get; }

    public TestSourceAuthorizationService AuthorizationService { get; }

    public TestAuthenticationStateProvider AuthenticationState { get; }

    public StudioMappedEndpoints MappedEndpoints { get; }

    public StudioState State => Services.GetRequiredService<StudioState>();

    /// <summary>The circuit's preferences (theme, time zone, clock), as the components see them; the time zone is pinned to UTC.</summary>
    public StudioPreferences Preferences => Services.GetRequiredService<StudioPreferences>();

    public StudioActionLog ActionLog => Services.GetRequiredService<StudioActionLog>();

    /// <summary>The ring's neutral way in and out: what the Activity page and the Overview read through.</summary>
    public StudioActivity ActivityRing => Services.GetRequiredService<StudioActivity>();

    public string CurrentUri => Services.GetRequiredService<NavigationManager>().Uri;

    /// <summary>Every capability on.</summary>
    public PostgresComponentContext WithAllCapabilities()
    {
        Options.Capabilities = PostgresCapabilities.All();
        return this;
    }

    /// <summary>The scope selector initialized over the real provider: the one data source, <c>default</c>.</summary>
    public async Task<PostgresComponentContext> ReadyAsync()
    {
        await State.EnsureInitializedAsync();
        return this;
    }

    public PostgresComponentContext Navigate(string relativeUri)
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo(relativeUri);
        return this;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (disposing)
        {
            DataSource.Dispose();
        }
    }
}
