using System.Security.Claims;

using JasperFx;

using Marten;

using MartenStudio.Integration.Tests.Logging;
using MartenStudio.SampleDomain;
using MartenStudio.Services;
using MartenStudio.Services.Health;

using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MartenStudio.Integration.Tests.Health;

/// <summary>
/// A studio container over the sample domain's store on the live Postgres, for the health checks' live
/// tests - and the shape a later packet's live tests reuse.
/// </summary>
/// <remarks>
/// <para>
/// <b>The healthy sample store</b> is <see cref="SampleStore.Configure" /> as it is, in a schema of the
/// test's own, in the Development environment. A check's quiet test runs it unchanged unless the check's own
/// recommendation is the change; a firing test that needs Marten's default where the sample departs from it says so
/// (K02's: the sample turns extended progression tracking on - see that test).
/// </para>
/// <para>
/// Everything goes through the studio's own services - <see cref="IHealthService" />, resolved in a DI scope
/// the way a circuit resolves it - so the scope resolution, the gates and the sweep cache are exercised
/// rather than bypassed. The log is captured at every level, so a test can say nothing reached Warning.
/// </para>
/// </remarks>
internal sealed class HealthLiveHost : IAsyncDisposable
{
    private readonly ServiceProvider provider;

    private HealthLiveHost(ServiceProvider provider, LogCapture logs, string schema)
    {
        this.provider = provider;
        Logs = logs;
        Schema = schema;
    }

    /// <summary>The host's log, Marten Studio's categories only.</summary>
    public LogCapture Logs { get; }

    /// <summary>The document schema this host owns.</summary>
    public string Schema { get; }

    /// <summary>The store.</summary>
    public IDocumentStore Store => provider.GetRequiredService<IDocumentStore>();

    /// <summary>The store's database as a whole, as the scope selector names it.</summary>
    public StudioScope Scope { get; } = new("default", string.Empty, null);

    /// <summary>The process-wide sweep cache, for a test that counts sweeps.</summary>
    public HealthSweepCache Cache => provider.GetRequiredService<HealthSweepCache>();

    /// <summary>The container, for a later packet's live test that needs a service of its own from it.</summary>
    public IServiceProvider Services => provider;

    /// <param name="postgres">The live container.</param>
    /// <param name="schema">A schema of the test's own; created empty (dropped first), with an <c>_events</c> twin.</param>
    /// <param name="environment">The host's environment, or <see langword="null" /> for a host that registers none.</param>
    /// <param name="store">Changes on top of the sample domain's configuration.</param>
    /// <param name="studio">The studio's options.</param>
    /// <param name="services">Anything else the container needs - an <c>IHealthStatementObserver</c>, say.</param>
    public static async Task<HealthLiveHost> CreateAsync(
        PostgresFixture postgres,
        string schema,
        string? environment = "Development",
        Action<StoreOptions>? store = null,
        Action<MartenStudioOptions>? studio = null,
        Action<IServiceCollection>? services = null)
    {
        await postgres.CreateSchemaAsync(schema);
        await postgres.CreateSchemaAsync(schema + "_events");

        LogCapture logs = new();
        ServiceCollection collection = new();

        collection.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Trace);
            builder.AddProvider(logs);
        });

        collection.AddAuthorization();
        collection.AddSingleton<AuthenticationStateProvider, Anonymous>();

        if (environment is not null)
        {
            collection.AddSingleton<IHostEnvironment>(new LiveHostEnvironment(environment));
        }

        collection.AddMarten(options =>
        {
            SampleStore.Configure(options, postgres.ConnectionString);
            options.DatabaseSchemaName = schema;
            options.Events.DatabaseSchemaName = schema + "_events";
            store?.Invoke(options);
        });

        collection.AddMartenStudio(options => studio?.Invoke(options));
        services?.Invoke(collection);

        return new HealthLiveHost(collection.BuildServiceProvider(), logs, schema);
    }

    /// <summary>The visitor's report, through a circuit-shaped scope of its own.</summary>
    public async Task<HealthReport> ReportAsync(HealthReportRequest? request = null)
    {
        await using AsyncServiceScope circuit = provider.CreateAsyncScope();

        return await circuit.ServiceProvider.GetRequiredService<IHealthService>()
            .GetReportAsync(Scope, request ?? HealthReportRequest.Regular, TestContext.Current.CancellationToken);
    }

    /// <summary>One check's status row, which a quiet test reads to prove the check really ran.</summary>
    public static HealthCheckStatus Status(HealthReport report, string code) =>
        report.Checks.Single(x => string.Equals(x.Code, code, StringComparison.Ordinal));

    public async ValueTask DisposeAsync()
    {
        await Store.DisposeAsync();
        await provider.DisposeAsync();
    }

    private sealed class Anonymous : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity())));
    }

    private sealed class LiveHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;

        public string ApplicationName { get; set; } = "MartenStudio.Integration.Tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}

/// <summary>What the configuration checks' live tests share: the AutoCreate a store was given.</summary>
internal static class HealthLiveStores
{
    /// <summary>The sample store with a different <c>AutoCreate</c>.</summary>
    public static Action<StoreOptions> WithAutoCreate(AutoCreate mode) => options => options.AutoCreateSchemaObjects = mode;
}
