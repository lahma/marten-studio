using Marten;

using MartenStudio.Services.Health;
using MartenStudio.Services.Health.Export;
using MartenStudio.Tests.Support;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using AspNetCoreHealthReport = Microsoft.Extensions.Diagnostics.HealthChecks.HealthReport;

namespace MartenStudio.Tests.Health;

/// <summary>
/// A real container with the studio, the host's ASP.NET Core health checks and the opt-in export, over a store
/// that never connects - with H1's scripted checks, a clock a test moves and a log a test reads.
/// </summary>
/// <remarks>
/// <para>
/// Its own rather than <see cref="HealthHarness" />, which is a visitor's circuit: it has no hook for the host's
/// <c>AddHealthChecks()</c> registrations, always registers the studio and always gives the store its default
/// connection - and these tests need a host that registered the check without the studio, tenancies of their
/// own, and run limits. The export has no visitor and is reached through <see cref="HealthCheckService" />,
/// exactly as a host's health endpoint reaches it.
/// </para>
/// <para>
/// Every connection string names port 1 on the loopback address, which refuses at once: a check, a tenancy or
/// an enumeration that reached for the database fails fast and on purpose - which is how the tenancy tests
/// prove that a probe opened nothing.
/// </para>
/// </remarks>
internal sealed class HealthExportHarness : IAsyncDisposable
{
    /// <summary>The database name in the default store's connection string, which must never reach the endpoint.</summary>
    public const string SecretDatabase = "export_secret_db";

    /// <summary>The name a registration gets when the caller gives none.</summary>
    public const string DefaultName = "marten-studio";

    /// <summary>The scripted checks the export runs: two configuration, one catalog, one activity, one deeper.</summary>
    public static readonly Type[] ExportChecks =
    [
        typeof(ScriptedConfigurationCheck),
        typeof(AnotherScriptedConfigurationCheck),
        typeof(ScriptedCatalogCheck),
        typeof(ScriptedActivityCheck),
        typeof(ScriptedDeeperCheck),
    ];

    private readonly ServiceProvider provider;

    private HealthExportHarness(ServiceProvider provider, FakeTimeProvider clock, CapturingLoggerProvider logs)
    {
        this.provider = provider;
        Clock = clock;
        Logs = logs;
    }

    public FakeTimeProvider Clock { get; }

    public CapturingLoggerProvider Logs { get; }

    public IServiceProvider Services => provider;

    public HealthScript Script => provider.GetRequiredService<HealthScript>();

    public HealthExportRunner Runner => provider.GetRequiredService<HealthExportRunner>();

    /// <summary>What the studio itself logged at Warning or above - its event ids, not ASP.NET Core's own lines.</summary>
    public IReadOnlyList<CapturedLogEntry> StudioWarnings =>
        [.. Logs.Entries.Where(static x => x.Level >= LogLevel.Warning && x.EventId.Id is >= 9200 and <= 9299)];

    /// <summary>A connection string that is refused at once, naming <paramref name="database" />.</summary>
    public static string Refused(string database) =>
        "Host=127.0.0.1;Port=1;Database=" + database + ";Username=none;Password=none;Timeout=2";

    /// <param name="checks">The advisor's checks: <see cref="ExportChecks" /> unless a test says otherwise.</param>
    /// <param name="studio">The studio's options.</param>
    /// <param name="tenancy">
    /// How the store reaches its databases; one refused default database, named <see cref="SecretDatabase" />,
    /// unless a test sets a tenancy of its own.
    /// </param>
    /// <param name="registerStudio">Whether the host called <c>AddMartenStudio()</c> (and <c>AddMarten()</c>) at all.</param>
    /// <param name="limits">How much a run may do, or the defaults.</param>
    /// <param name="healthChecks">The host's health check registrations; one default <c>AddMartenStudio()</c> unless given.</param>
    /// <param name="lifetime">The host's lifetime, for a test that stops the application.</param>
    /// <param name="hostServices">Anything else the host registers - a projection coordinator, say.</param>
    /// <param name="validateScopes">
    /// Whether the container refuses a scoped service resolved from the root, as a Development host's does
    /// (<c>ServiceProviderOptions.ValidateScopes</c>).
    /// </param>
    public static HealthExportHarness Create(
        IEnumerable<Type>? checks = null,
        Action<MartenStudioOptions>? studio = null,
        Action<StoreOptions>? tenancy = null,
        bool registerStudio = true,
        HealthExportLimits? limits = null,
        Action<IHealthChecksBuilder>? healthChecks = null,
        IHostApplicationLifetime? lifetime = null,
        Action<IServiceCollection>? hostServices = null,
        bool validateScopes = false)
    {
        FakeTimeProvider clock = new(new DateTimeOffset(2026, 10, 15, 9, 0, 0, TimeSpan.Zero));
        CapturingLoggerProvider logs = new();

        ServiceCollection services = new();
        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Trace);
            builder.AddProvider(logs);
        });

        services.AddSingleton<TimeProvider>(clock);
        services.AddSingleton(new HealthScript());

        if (limits is not null)
        {
            services.AddSingleton(limits);
        }

        if (lifetime is not null)
        {
            services.AddSingleton(lifetime);
        }

        if (registerStudio)
        {
            services.AddSingleton(HealthCheckRegistry.ForTypes(checks ?? ExportChecks));

            services.AddMarten(options =>
            {
                options.DatabaseSchemaName = "health_export";

                if (tenancy is null)
                {
                    options.Connection(Refused(SecretDatabase));
                }
                else
                {
                    tenancy(options);
                }
            });

            services.AddMartenStudio(options => studio?.Invoke(options));
        }

        IHealthChecksBuilder builder = services.AddHealthChecks();

        if (healthChecks is null)
        {
            builder.AddMartenStudio();
        }
        else
        {
            healthChecks(builder);
        }

        hostServices?.Invoke(services);

        return new HealthExportHarness(
            services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = validateScopes }),
            clock,
            logs);
    }

    /// <summary>One probe of every registration, as a health endpoint makes it; the entry named <paramref name="name" />.</summary>
    public async Task<HealthReportEntry> ProbeAsync(string name = DefaultName)
    {
        AspNetCoreHealthReport report = await provider.GetRequiredService<HealthCheckService>()
            .CheckHealthAsync(TestContext.Current.CancellationToken);

        return report.Entries[name];
    }

    /// <summary>One probe of every registration.</summary>
    public Task<AspNetCoreHealthReport> ProbeAllAsync() =>
        provider.GetRequiredService<HealthCheckService>().CheckHealthAsync(TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync() => provider.DisposeAsync();
}

/// <summary>A host lifetime a test stops by hand.</summary>
internal sealed class StoppableLifetime : IHostApplicationLifetime, IDisposable
{
    private readonly CancellationTokenSource stopping = new();

    public CancellationToken ApplicationStarted => CancellationToken.None;

    public CancellationToken ApplicationStopping => stopping.Token;

    public CancellationToken ApplicationStopped => CancellationToken.None;

    public void StopApplication() => stopping.Cancel();

    public void Dispose() => stopping.Dispose();
}
