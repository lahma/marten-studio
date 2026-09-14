using System.Diagnostics;
using System.Runtime.CompilerServices;

using MartenStudio.SampleDomain.Generation;

namespace MartenStudio.Integration.Tests.Generation;

/// <summary>
/// One generated data set, shared by every test in a class.
/// </summary>
/// <remarks>
/// <para>
/// A class fixture, because generating a hundred thousand rows per test method would be a suite nobody
/// runs. The tests in a class therefore share a database - which is fine here, because every test in
/// these classes reads. The one that writes (truncation) has a class of its own.
/// </para>
/// <para>
/// The store is built by <see cref="MartenFixture" />, so everything is exercised through the studio's
/// own container: the collections rail, the pages and the projections read all come out of services
/// resolved from a scope, and every one of them goes through <c>StudioScopeResolver</c> first. A test
/// that reached for a connection would be measuring Postgres.
/// </para>
/// </remarks>
public abstract class GeneratedDataFixtureBase : IAsyncLifetime
{
    private readonly PostgresFixture postgres;

    /// <summary>Wires the class fixture to the assembly's one Postgres.</summary>
    /// <param name="postgres">The container fixture, injected by xunit.</param>
    protected GeneratedDataFixtureBase(PostgresFixture postgres) => this.postgres = postgres;

    /// <summary>The schema this class owns.</summary>
    public abstract string Schema { get; }

    /// <summary>How much to generate.</summary>
    internal abstract DemoDataPlan Plan { get; }

    /// <summary>Whether this fixture does anything at all. The large set is opt-in.</summary>
    protected virtual bool Enabled => DockerAvailability.IsAvailable;

    /// <summary>The store, the studio's container and the scope, once generation has run.</summary>
    internal MartenFixture Marten => marten
        ?? throw new InvalidOperationException(
            "The generated data set was not built. A test that needs it must skip when Docker is absent "
            + "(and, for the large set, when MARTENSTUDIO_LARGE is not 1).");

    /// <summary>How long the generation took, wall clock.</summary>
    public TimeSpan GenerationTime { get; private set; }

    /// <summary>What the generator says it wrote.</summary>
    internal DemoDataCounters Counters { get; private set; }

    /// <summary>The run marker every generated document carries.</summary>
    public string RunId { get; private set; } = string.Empty;

    /// <summary>Rows (documents plus events) per second over the whole run.</summary>
    public double RowsPerSecond => GenerationTime.TotalSeconds > 0
        ? Counters.Rows / GenerationTime.TotalSeconds
        : 0;

    private MartenFixture? marten;

    /// <summary>Creates the schema, seeds the sample data and generates the set.</summary>
    public async ValueTask InitializeAsync()
    {
        if (!Enabled)
        {
            return;
        }

        await postgres.CreateSchemaAsync(Schema);
        await postgres.CreateSchemaAsync(Schema + "_events");

        marten = await MartenFixture.CreateAsync(
            postgres.ConnectionString,
            Schema,
            options =>
            {
                options.DatabaseSchemaName = Schema;
                options.Events.DatabaseSchemaName = Schema + "_events";
            });

        // The seeder first, so that every test in these classes has both kinds of row: twenty-five
        // hand-seeded customers with no marker, and however many generated ones with it. That is what
        // makes "truncation removed only the generated rows" an assertion rather than a hope.
        await marten.SeedSampleDataAsync();

        var generator = new DemoDataGenerator(marten.Store, Plan);
        RunId = generator.RunId;

        long started = Stopwatch.GetTimestamp();
        Counters = await generator.GenerateAsync(progress: null, CancellationToken.None);
        GenerationTime = Stopwatch.GetElapsedTime(started);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);

        if (marten is not null)
        {
            await marten.DisposeAsync();
        }
    }
}

/// <summary>The ~100 000 document, ~100 000 event set the always-on responsiveness tests run against.</summary>
/// <param name="postgres">The assembly's Postgres, injected by xunit.</param>
public sealed class MediumDataSetFixture(PostgresFixture postgres) : GeneratedDataFixtureBase(postgres)
{
    /// <inheritdoc />
    public override string Schema => "generation_medium";

    /// <inheritdoc />
    internal override DemoDataPlan Plan => DemoDataPlan.For(DemoDataSize.Medium);
}

/// <summary>
/// The ~1.2 million document, ~1.2 million event set, built only when <c>MARTENSTUDIO_LARGE=1</c>.
/// </summary>
/// <param name="postgres">The assembly's Postgres, injected by xunit.</param>
public sealed class LargeDataSetFixture(PostgresFixture postgres) : GeneratedDataFixtureBase(postgres)
{
    /// <inheritdoc />
    public override string Schema => "generation_large";

    /// <inheritdoc />
    internal override DemoDataPlan Plan => DemoDataPlan.For(DemoDataSize.Large);

    /// <inheritdoc />
    protected override bool Enabled => LargeDataSet.IsEnabled;
}

/// <summary>
/// The switch that turns the large data set on.
/// </summary>
/// <remarks>
/// Opt-in rather than opt-out, because generating and then measuring 2.4 million rows takes minutes and
/// several gigabytes of disk. A suite that did that on every <c>dotnet test</c> would be a suite people
/// stop running, and a responsiveness test nobody runs measures nothing.
/// </remarks>
public static class LargeDataSet
{
    /// <summary>The variable that turns the large data set on. Set it to <c>1</c>.</summary>
    public const string EnvironmentVariable = "MARTENSTUDIO_LARGE";

    /// <summary>Why the large tests skipped.</summary>
    public const string SkipReason =
        "The large (1.2M document / 1.2M event) data set is opt-in: set MARTENSTUDIO_LARGE=1 and run with "
        + "Docker available. It takes several minutes and several GB of disk.";

    /// <summary>Whether the large set is built and its tests run.</summary>
    public static bool IsEnabled =>
        DockerAvailability.IsAvailable
        && string.Equals(Environment.GetEnvironmentVariable(EnvironmentVariable)?.Trim(), "1", StringComparison.Ordinal);
}

/// <summary>A fact that needs the large data set, and skips with a reason when it is not switched on.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class LargeDataFactAttribute : FactAttribute
{
    /// <summary>Wires up xunit's conditional skip against <see cref="LargeDataSet.IsEnabled" />.</summary>
    /// <param name="sourceFilePath">Supplied by the compiler; xunit uses it to locate the test.</param>
    /// <param name="sourceLineNumber">Supplied by the compiler; xunit uses it to locate the test.</param>
    public LargeDataFactAttribute(
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        Skip = LargeDataSet.SkipReason;
        SkipType = typeof(LargeDataSet);
        SkipUnless = nameof(LargeDataSet.IsEnabled);
    }
}
