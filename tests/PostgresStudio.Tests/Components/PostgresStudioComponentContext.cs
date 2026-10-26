using Bunit;

using MartenStudio.Tests.Health;
using MartenStudio.Tests.Support;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Npgsql;

using PostgresStudio.Internal;
using PostgresStudio.Services;
using PostgresStudio.Services.Database;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Live;
using PostgresStudio.Services.Postgres;
using PostgresStudio.Services.Relationships;
using PostgresStudio.Services.Settings;
using PostgresStudio.Services.Sources;
using PostgresStudio.Tests.Support;

namespace PostgresStudio.Tests.Components;

/// <summary>
/// A bUnit context wired as <c>AddPostgresStudio()</c> wires a circuit: PostgresStudio's settings, descriptor, links and
/// source provider - the real ones, over one <see cref="NpgsqlDataSource" /> registered as the application's unkeyed
/// singleton, which nothing here ever opens - and fakes for every data service a page reads through.
/// </summary>
/// <remarks>
/// The scope catalog is the real one over the real provider, so the header, the frame and every page see the scope a
/// host with one data source would see: <c>?store=default&amp;db=default</c>. What a page reads is the fakes'.
/// </remarks>
internal sealed class PostgresStudioComponentContext : BunitContext
{
    /// <summary>A connection string nothing connects to: no test here opens a connection.</summary>
    public const string ConnectionString = "Host=127.0.0.1;Port=1;Database=app;Username=app;Password=secret;Timeout=1";

    public PostgresStudioComponentContext(Action<IServiceCollection>? configure = null)
    {
        Options = new PostgresStudioOptions();
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
        Services.AddSingleton<IOptions<PostgresStudioOptions>>(Microsoft.Extensions.Options.Options.Create(Options));
        Services.AddSingleton<IStudioSettings>(static provider =>
            new PostgresStudioSettings(provider.GetRequiredService<IOptions<PostgresStudioOptions>>()));
        Services.AddSingleton(PostgresStudioDescriptor.Instance);
        Services.AddSingleton<IStudioLinks, PostgresStudioLinks>();
        Services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor());

        // The real source provider and scope catalog over the one data source above.
        Services.AddSingleton(provider => new PostgresSourceRegistry(Services, provider));
        Services.AddSingleton<IStudioSourceProvider, PostgresStudioSourceProvider>();
        Services.AddSingleton<IStudioScopeCatalog, StudioScopeCatalog>();

        Services.AddSingleton<IDatabaseObjectService>(DatabaseObjects);
        Services.AddSingleton<ITableRowService>(TableRows);
        Services.AddSingleton<IRelationshipDataService>(RelationshipData);
        Services.AddSingleton<ISessionActivityService>(Sessions);
        Services.AddSingleton<PostgresStudio.Services.Storage.IStorageService>(Storage);
        Services.AddSingleton<ISqlConsoleService>(SqlConsole);
        Services.AddSingleton<IPostgresServerReader>(new FakeServerReader());
        Services.AddSingleton<INavIndicatorService>(new FakeNavIndicators());
        Services.AddSingleton(new DatabaseBrowserState());
        Services.AddSingleton(new SessionPoolObserver("PostgresStudio.Tests." + Guid.NewGuid().ToString("N")));

        Services.AddScoped<StudioLiveUpdatesFactory>();

        Services.AddSingleton<IHealthService>(Health);
        Services.AddSingleton<HealthMutes>();
        Services.AddSingleton<HealthAlerts>();

        Services.AddSingleton<IAuthorizationService>(AuthorizationService);
        Services.AddSingleton<AuthenticationStateProvider>(AuthenticationState);
        Services.AddSingleton<StudioAuthorization>();

        Services.AddSingleton(static provider =>
        {
            StudioState state = ActivatorUtilities.CreateInstance<StudioState>(provider);
            state.SelectedTimeZoneId = TimeZoneInfo.Utc.Id;
            return state;
        });

        Services.AddSingleton<StudioCapabilityGuard>();
        Services.AddSingleton<ToastService>();
        Services.AddSingleton<StudioActionLogService>();
        Services.AddSingleton<StudioActionLog>();

        MappedEndpoints = new StudioMappedEndpoints();
        Services.AddSingleton(MappedEndpoints);

        Services.AddSingleton<ComponentStatePersistenceManager>();
        Services.AddSingleton(static provider =>
            provider.GetRequiredService<ComponentStatePersistenceManager>().State);

        configure?.Invoke(Services);
    }

    public PostgresStudioOptions Options { get; }

    public NpgsqlDataSource DataSource { get; }

    public FakeDatabaseObjectService DatabaseObjects { get; }

    public FakeTableRowService TableRows { get; }

    public FakeRelationshipDataService RelationshipData { get; }

    public FakeHealthService Health { get; }

    public FakeSessions Sessions { get; }

    public FakeStorage Storage { get; }

    public FakeSqlConsole SqlConsole { get; }

    public TestSourceAuthorizationService AuthorizationService { get; }

    public TestAuthenticationStateProvider AuthenticationState { get; }

    public StudioMappedEndpoints MappedEndpoints { get; }

    public StudioState State => Services.GetRequiredService<StudioState>();

    public StudioActionLog ActionLog => Services.GetRequiredService<StudioActionLog>();

    public string CurrentUri => Services.GetRequiredService<NavigationManager>().Uri;

    /// <summary>Every capability on.</summary>
    public PostgresStudioComponentContext WithAllCapabilities()
    {
        Options.Capabilities = PostgresStudioCapabilities.All();
        return this;
    }

    /// <summary>The scope selector initialized over the real provider: the one data source, <c>default</c>.</summary>
    public async Task<PostgresStudioComponentContext> ReadyAsync()
    {
        await State.EnsureInitializedAsync();
        return this;
    }

    public PostgresStudioComponentContext Navigate(string relativeUri)
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo(relativeUri);
        return this;
    }

    public new Task DisposeComponentsAsync() => Renderer.Dispatcher.InvokeAsync(base.DisposeComponentsAsync);

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (disposing)
        {
            DataSource.Dispose();
        }
    }
}
