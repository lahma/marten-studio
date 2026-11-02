using Marten;

using MartenStudio.Services;
using PostgresStudio.Services.Sources;
using MartenStudio.Tests.Support;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PostgresStudio.Services;
using PostgresStudio.Services.Health;

namespace MartenStudio.Tests.Health;

/// <summary>What a scripted check read.</summary>
/// <param name="Code">Which check.</param>
/// <param name="Parts">What it contributes to the sample.</param>
/// <param name="Judge">How its evaluator judges it - the script's, so a test says the verdict outright.</param>
internal sealed record ScriptedFacts(
    string Code,
    IReadOnlyList<HealthSamplePart> Parts,
    Func<ScriptedFacts, HealthEvaluation, HealthVerdict>? Judge);

/// <summary>
/// What each scripted check does, by code: registered in the container, so a stateless check reads it from
/// its context like a real gatherer reads a host service.
/// </summary>
internal sealed class HealthScript
{
    public Dictionary<string, Func<HealthCheckContext, CancellationToken, Task<ScriptedFacts>>> Gather { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, Func<ScriptedFacts, HealthEvaluation, HealthVerdict>> Judge { get; } = new(StringComparer.Ordinal);

    /// <summary>How many times each code's gatherer ran.</summary>
    public Dictionary<string, int> Runs { get; } = new(StringComparer.Ordinal);

    /// <summary>Has <paramref name="code" /> find one finding of <paramref name="severity" />.</summary>
    public HealthScript Finds(string code, HealthSeverity severity, string title = "Something is wrong", Action<HealthFindingBuilder>? shape = null)
    {
        Judge[code] = (_, _) =>
        {
            HealthFindingBuilder builder = new(code, severity, title);
            shape?.Invoke(builder);
            return HealthVerdict.Of(builder.Build());
        };

        return this;
    }

    /// <summary>Has <paramref name="code" />'s gatherer throw.</summary>
    public HealthScript Throws(string code, Exception exception)
    {
        Gather[code] = (_, _) => Task.FromException<ScriptedFacts>(exception);
        return this;
    }
}

/// <summary>A finding for a scripted check, shaped by a test.</summary>
internal sealed class HealthFindingBuilder(string code, HealthSeverity severity, string title)
{
    public string? Subject { get; set; }

    public string? TenantId { get; set; }

    public List<HealthEvidence> Evidence { get; } = [];

    public List<Type> DocumentTypes { get; } = [];

    public List<string> GatedSchemas { get; } = [];

    public HealthRemedy? Remedy { get; set; }

    public HealthFinding Build() => new()
    {
        Code = code,
        Severity = severity,
        Title = title,
        WhyItMatters = "Scripted.",
        Subject = Subject,
        TenantId = TenantId,
        Evidence = [.. Evidence],
        DocumentTypes = [.. DocumentTypes],
        GatedSchemas = [.. GatedSchemas],
        Remedy = Remedy,
    };
}

/// <summary>
/// The base of the scripted checks: a real <see cref="HealthCheck{TFacts}" />, stateless, whose gatherer and
/// evaluator do what the container's <see cref="HealthScript" /> says for its code.
/// </summary>
internal abstract class ScriptedCheck : HealthCheck<ScriptedFacts>
{
    private string Code => HealthCheckRegistry.Describe(GetType()).Code;

    public override async Task<ScriptedFacts> GatherAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        HealthScript script = context.Services.GetRequiredService<HealthScript>();
        string code = Code;

        script.Runs[code] = script.Runs.GetValueOrDefault(code) + 1;

        ScriptedFacts facts = script.Gather.TryGetValue(code, out var gather)
            ? await gather(context, cancellationToken)
            : new ScriptedFacts(code, [], null);

        return facts with { Judge = script.Judge.GetValueOrDefault(code) };
    }

    public override IEnumerable<HealthSamplePart> Sample(ScriptedFacts facts) => facts.Parts;

    public override HealthVerdict Evaluate(ScriptedFacts facts, HealthEvaluation evaluation) =>
        facts.Judge?.Invoke(facts, evaluation) ?? HealthVerdict.Quiet("scripted: nothing to say");
}

[HealthCheck("K90", HealthArea.Configuration, HealthCostTier.InProcess, "Scripted configuration", Gate = HealthGate.None)]
internal sealed class ScriptedConfigurationCheck : ScriptedCheck;

[HealthCheck("K91", HealthArea.Configuration, HealthCostTier.InProcess, "Another scripted configuration", Gate = HealthGate.None)]
internal sealed class AnotherScriptedConfigurationCheck : ScriptedCheck;

[HealthCheck("T90", HealthArea.Tables, HealthCostTier.Catalog, "Scripted catalog")]
internal sealed class ScriptedCatalogCheck : ScriptedCheck;

[HealthCheck("C90", HealthArea.Connections, HealthCostTier.Catalog, "Scripted activity", Gate = HealthGate.Activity)]
internal sealed class ScriptedActivityCheck : ScriptedCheck;

[HealthCheck("E90", HealthArea.Events, HealthCostTier.BoundedData, "Scripted deeper")]
internal sealed class ScriptedDeeperCheck : ScriptedCheck;

/// <summary>
/// A check that lights up on a feature of a Marten that does not exist yet (<see cref="ScriptedStudioRuntime.Feature" />,
/// Marten 9.99): the probe table is empty at 1.1.0, so the engine's <c>RequiresFeature</c> path is exercised through a
/// runtime that adds this one feature to it. A test that uses it passes <c>studioRuntime:</c> to the harness, with a
/// made-up Marten that has the feature or lacks it.
/// </summary>
[HealthCheck("D90", HealthArea.Daemon, HealthCostTier.Catalog, "Scripted light-up",
    RequiresFeature = ScriptedStudioRuntime.FeatureName)]
internal sealed class ScriptedLightUpCheck : ScriptedCheck;

/// <summary>
/// MartenStudio's runtime plus one made-up feature, found by name - so a check can require a feature while the real probe
/// table is empty. Everything else is MartenStudio's own runtime's answer.
/// </summary>
internal sealed class ScriptedStudioRuntime(MartenStudio.Services.Configuration.MartenRuntime runtime) : IStudioRuntime
{
    public const string FeatureName = "ScriptedFeature";

    private readonly MartenStudio.Services.Configuration.MartenStudioRuntime inner = new(runtime);

    /// <summary>A feature of Marten 9.99, built over the probe table's private constructor.</summary>
    public static MartenStudio.Services.Configuration.RuntimeFeature Feature { get; } =
        (MartenStudio.Services.Configuration.RuntimeFeature) typeof(MartenStudio.Services.Configuration.RuntimeFeature)
            .GetConstructors(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            .Single()
            .Invoke([FeatureName, MartenStudio.Services.Configuration.RuntimePackage.Marten, "9.99.0", "9.99.0", "A feature of a Marten that is not out yet.", null]);

    /// <summary>A Marten that has it.</summary>
    public static MartenStudio.Services.Configuration.MartenRuntime HostWithIt { get; } =
        MartenStudio.Services.Configuration.MartenRuntime.ForVersions(new Dictionary<MartenStudio.Services.Configuration.RuntimePackage, string>
        {
            [MartenStudio.Services.Configuration.RuntimePackage.Marten] = "9.99.0",
        });

    /// <summary>A Marten that lacks it.</summary>
    public static MartenStudio.Services.Configuration.MartenRuntime HostWithout { get; } =
        MartenStudio.Services.Configuration.MartenRuntime.ForVersions(new Dictionary<MartenStudio.Services.Configuration.RuntimePackage, string>
        {
            [MartenStudio.Services.Configuration.RuntimePackage.Marten] = "9.57.0",
        });

    public StudioFeatureRequirement? FindFeature(string name) =>
        string.Equals(name, FeatureName, StringComparison.Ordinal) ? Feature : inner.FindFeature(name);

    public bool IsAvailable(StudioFeatureRequirement feature) => inner.IsAvailable(feature);

    public string DescribeHost(StudioFeatureRequirement feature) => inner.DescribeHost(feature);
}

/// <summary>A document type a test hides with <c>IsDocumentTypeVisible</c>.</summary>
public sealed class HiddenHealthThing
{
    public Guid Id { get; set; }
}

/// <summary>An environment the host may or may not register.</summary>
internal sealed class TestHostEnvironment(string environmentName) : IHostEnvironment
{
    public string EnvironmentName { get; set; } = environmentName;

    public string ApplicationName { get; set; } = "MartenStudio.Tests";

    public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}

/// <summary>
/// A real studio container over a store that never connects, with scripted checks, a clock a test moves and a
/// log a test reads.
/// </summary>
internal sealed class HealthHarness : IAsyncDisposable
{
    /// <summary>Refused at once rather than looked up: a check that reaches for SQL fails fast and on purpose.</summary>
    public const string RefusedConnectionString = "Host=127.0.0.1;Port=1;Database=none;Username=none;Password=none;Timeout=2";

    /// <summary>The store policy <see cref="Create" /> configures when asked to.</summary>
    public const string StorePolicy = "store-policy";

    /// <summary>The write policy <see cref="Create" /> configures when asked to.</summary>
    public const string WritePolicy = "write-policy";

    /// <summary>Every scripted check.</summary>
    public static readonly Type[] ScriptedChecks =
    [
        typeof(ScriptedConfigurationCheck),
        typeof(AnotherScriptedConfigurationCheck),
        typeof(ScriptedCatalogCheck),
        typeof(ScriptedActivityCheck),
        typeof(ScriptedDeeperCheck),
    ];

    private readonly ServiceProvider provider;
    private readonly AsyncServiceScope circuit;

    private HealthHarness(ServiceProvider provider, FakeTimeProvider clock, CapturingLoggerProvider logs, TestStoreAuthorizationService policies)
    {
        this.provider = provider;
        circuit = provider.CreateAsyncScope();
        Clock = clock;
        Logs = logs;
        Policies = policies;
    }

    public FakeTimeProvider Clock { get; }

    public CapturingLoggerProvider Logs { get; }

    public TestStoreAuthorizationService Policies { get; }

    public HealthScript Script => provider.GetRequiredService<HealthScript>();

    /// <summary>The root container, for a later packet's test that needs a service of its own from it.</summary>
    public IServiceProvider Services => provider;

    /// <summary>The circuit-shaped scope the harness's scoped services come from.</summary>
    public IServiceProvider Circuit => circuit.ServiceProvider;

    public HealthSweeper Sweeper => provider.GetRequiredService<HealthSweeper>();

    public HealthSweepCache Cache => provider.GetRequiredService<HealthSweepCache>();

    public HealthHistory History => provider.GetRequiredService<HealthHistory>();

    public IDocumentStore Store => provider.GetRequiredService<IDocumentStore>();

    /// <summary>The circuit's health service.</summary>
    public IHealthService Health => circuit.ServiceProvider.GetRequiredService<IHealthService>();

    /// <summary>The circuit's mutes.</summary>
    public HealthMutes Mutes => circuit.ServiceProvider.GetRequiredService<HealthMutes>();

    /// <summary>The default store's database as a whole, as a visitor's scope names it.</summary>
    public StudioScope Scope => new("default", Store.Storage.Database.Id.Identity, null);

    /// <summary>The database, resolved with no tenant, as the service hands it to a sweep.</summary>
    public ResolvedScope Resolved => new(
        Scope,
        new MartenStoreRegistration("default", "Default", typeof(IDocumentStore)),
        Store,
        Store.Storage.Database);

    /// <param name="checks">The checks the registry holds: the scripted ones unless a test says otherwise.</param>
    /// <param name="studio">The studio's options.</param>
    /// <param name="store">The store's options, on top of the refused connection.</param>
    /// <param name="environment">The host's environment, or <see langword="null" /> for a host that registers none.</param>
    /// <param name="policies">Whether the store and write policies are configured (and so asked).</param>
    /// <param name="runtime">
    /// The Marten the sweeper and the health service believe this host runs - a made-up one, so a light-up check's
    /// applicability is the same at every Marten the suite runs at - or <see langword="null" /> for this process's own.
    /// </param>
    /// <param name="sourceKinds">
    /// The kinds of source each check runs against (<see langword="null" /> for every kind), or <see langword="null" /> to
    /// run every check against every kind.
    /// </param>
    /// <param name="before">
    /// Registrations a test makes before <c>AddMartenStudio()</c>, whose <c>TryAdd</c> then keeps them - a seam's
    /// stand-in, say.
    /// </param>
    /// <param name="studioRuntime">
    /// A runtime that answers for the checks' features instead of MartenStudio's own (see <see cref="ScriptedStudioRuntime" />),
    /// or <see langword="null" />.
    /// </param>
    public static HealthHarness Create(
        IEnumerable<Type>? checks = null,
        Action<MartenStudioOptions>? studio = null,
        Action<StoreOptions>? store = null,
        string? environment = "Development",
        bool policies = false,
        MartenStudio.Services.Configuration.MartenRuntime? runtime = null,
        Func<Type, IReadOnlySet<StudioSourceKind>?>? sourceKinds = null,
        Action<IServiceCollection>? before = null,
        IStudioRuntime? studioRuntime = null)
    {
        FakeTimeProvider clock = new(new DateTimeOffset(2026, 10, 15, 9, 0, 0, TimeSpan.Zero));
        CapturingLoggerProvider logs = new();
        TestStoreAuthorizationService authorization = new();
        TestAuthenticationStateProvider users = new();
        users.SignIn("operator");

        ServiceCollection services = new();
        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Trace);
            builder.AddProvider(logs);
        });

        services.AddSingleton<TimeProvider>(clock);
        services.AddSingleton(new HealthScript());

        if (runtime is not null)
        {
            // HealthSweeper and HealthService take an optional MartenRuntime; registered, it is the one they ask.
            services.AddSingleton(runtime);
        }
        if (studioRuntime is not null)
        {
            services.AddSingleton(studioRuntime);
        }

        services.AddSingleton(HealthCheckRegistry.ForTypes(
            (checks ?? ScriptedChecks).Select(type => (type, sourceKinds?.Invoke(type))),
            studioRuntime ?? MartenStudio.Services.Configuration.MartenStudioRuntime.Current));

        if (environment is not null)
        {
            services.AddSingleton<IHostEnvironment>(new TestHostEnvironment(environment));
        }

        services.AddMarten(options =>
        {
            // A pool of this harness's own, as a real host's data source is: Npgsql names an unnamed pool by its
            // connection string, and every harness in the process sharing one would make C01's pool ambiguous.
            options.Connection(RefusedConnectionString + ";Application Name=MartenStudio.Tests." + Guid.NewGuid().ToString("N"));
            options.DatabaseSchemaName = "health_unit";
            store?.Invoke(options);
        });

        before?.Invoke(services);

        services.AddMartenStudio(options =>
        {
            if (policies)
            {
                options.StoreAuthorizationPolicy = StorePolicy;
                options.WriteAuthorizationPolicy = WritePolicy;
            }

            studio?.Invoke(options);
        });

        services.AddSingleton<IAuthorizationService>(authorization);
        services.AddScoped<AuthenticationStateProvider>(_ => users);

        return new HealthHarness(services.BuildServiceProvider(), clock, logs, authorization);
    }

    /// <summary>One regular sweep, straight through the sweeper.</summary>
    public Task<HealthSweep> SweepAsync(HealthSweepKind kind = HealthSweepKind.Regular) =>
        Sweeper.SweepAsync(Resolved, kind, Xunit.TestContext.Current.CancellationToken);

    /// <summary>Lines at Warning or above.</summary>
    public IReadOnlyList<CapturedLogEntry> Warnings => [.. Logs.Entries.Where(static x => x.Level >= LogLevel.Warning)];

    public async ValueTask DisposeAsync()
    {
        await circuit.DisposeAsync();
        await provider.DisposeAsync();
    }
}
