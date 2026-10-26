using Marten;

using Microsoft.Extensions.DependencyInjection;

using Npgsql;
using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Schema;

namespace MartenStudio.Integration.Tests.Health;

/// <summary>A document the scan, HOT, vacuum and statistics live tests fill by the hundred thousand.</summary>
public sealed class HealthScanThing
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public int Rank { get; set; }
}

/// <summary>A document whose indexes the index live tests shape.</summary>
public sealed class HealthIndexedThing
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public int Rank { get; set; }
}

/// <summary>A soft-deleted document, for T10.</summary>
public sealed class HealthTombstoneThing
{
    public Guid Id { get; set; }

    public int Rank { get; set; }
}

/// <summary>A document with an <c>int</c> id, which Marten draws from HiLo, for T11.</summary>
public sealed class HealthCounterThing
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;
}

/// <summary>A document the hidden-type test hides with <c>IsDocumentTypeVisible</c>.</summary>
public sealed class HealthSecretThing
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public int Rank { get; set; }
}

/// <summary>A conjoined document partitioned by tenant, for the no-lock sweep.</summary>
public sealed class HealthTenantThing
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;
}

/// <summary>A clock the test moves, so two sweeps a test asks for are an interval apart.</summary>
/// <remarks>
/// Registered as the container's <see cref="TimeProvider" />, which the sweep cache and the sweeper both take:
/// moving it past <c>HealthInterval</c> makes the next report a fresh sweep, and the two samples it leaves are
/// a rate. Only <see cref="GetUtcNow" /> is used by either; nothing here schedules a timer.
/// </remarks>
internal sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset now = start;

    public override DateTimeOffset GetUtcNow() => now;

    public void Advance(TimeSpan by) => now += by;
}

/// <summary>
/// What the tables-and-indexes live tests share: a host on a moving clock, the data they produce by plain SQL in
/// the test's own schema, and a statistics flush that makes the counters deterministic.
/// </summary>
/// <remarks>
/// <para>
/// <b>Wait on the condition, never on the clock.</b> A backend sends its statistics to shared memory at most once
/// a second, so a counter read straight after a write may not have moved yet. <c>pg_stat_force_next_flush()</c>
/// (PostgreSQL 15+) on the connection that did the writing makes the next idle moment flush them, and Postgres
/// flushes before it tells the client it is ready - so once the call returns, any other session reads the new
/// counters. Every helper that writes ends with it.
/// </para>
/// <para>
/// Everything here is the test's own DDL and DML in the test's own schema. The studio runs none of it.
/// </para>
/// </remarks>
internal static class TableHealthLive
{
    /// <summary>When every manual clock starts.</summary>
    public static readonly DateTimeOffset Start = new(2026, 10, 15, 9, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// A studio clock eight days past the server's, for T01, T05 and T06, which give no usage verdict over statistics under
    /// seven days old (issue #8 G4) - and a fresh container's are minutes old. Paired with an <c>ANALYZE</c> of a table the
    /// sweep judges, so that a database whose own reset time is null (15+) records a moment its counters cover (W5-B1).
    /// </summary>
    public static DateTimeOffset Covering => DateTimeOffset.UtcNow.AddDays(8);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>A host whose sweeps follow <paramref name="clock" />.</summary>
    public static Task<HealthLiveHost> HostAsync(
        PostgresFixture postgres,
        string schema,
        ManualClock clock,
        Action<StoreOptions>? store = null,
        Action<MartenStudioOptions>? studio = null,
        Action<IServiceCollection>? services = null) =>
        HealthLiveHost.CreateAsync(
            postgres,
            schema,
            store: store,
            studio: studio,
            services: collection =>
            {
                collection.AddSingleton<TimeProvider>(clock);
                services?.Invoke(collection);
            });

    /// <summary>Applies the store's configuration - the test's DDL, in the test's schemas.</summary>
    public static async Task ApplyAsync(IDocumentStore store) =>
        await store.Storage.ApplyAllConfiguredChangesToDatabaseAsync();

    /// <summary>The quoted, qualified table of <typeparamref name="T" />.</summary>
    public static string TableOf<T>(IDocumentStore store)
    {
        var name = store.Options.FindOrResolveDocumentType(typeof(T)).TableName;
        return SqlIdentifier.Qualify(name.Schema, name.Name);
    }

    /// <summary>The <c>schema.table</c> key a finding's subject names <typeparamref name="T" />'s table by.</summary>
    public static string KeyOf<T>(IDocumentStore store)
    {
        var name = store.Options.FindOrResolveDocumentType(typeof(T)).TableName;
        return SchemaKey.For(name.Schema, name.Name);
    }

    /// <summary>Runs statements on one connection of the test's own, then flushes its statistics.</summary>
    public static async Task ExecuteAsync(PostgresFixture postgres, params string[] statements)
    {
        await using NpgsqlConnection connection = await postgres.OpenAsync(Token);

        foreach (string sql in statements)
        {
            await using NpgsqlCommand command = new(sql, connection) { CommandTimeout = 300 };
            await command.ExecuteNonQueryAsync(Token);
        }

        await FlushStatisticsAsync(connection);
    }

    /// <summary>
    /// <paramref name="count" /> documents straight into a Marten table: every metadata column has a default, so
    /// <c>(id, data)</c> is a whole row.
    /// </summary>
    public static Task InsertAsync(PostgresFixture postgres, string table, int count, string data = "jsonb_build_object('Name', 'n' || g, 'Rank', g)") =>
        ExecuteAsync(
            postgres,
            "insert into " + table + " (id, data) select gen_random_uuid(), " + data + " from generate_series(1, "
            + count.ToString(System.Globalization.CultureInfo.InvariantCulture) + ") as g");

    /// <summary>Makes this backend's pending statistics visible to every other session before the call returns.</summary>
    public static async Task FlushStatisticsAsync(NpgsqlConnection connection)
    {
        await using NpgsqlCommand flush = new("select pg_catalog.pg_stat_force_next_flush()", connection);
        await flush.ExecuteNonQueryAsync(Token);
    }

    /// <summary>The findings of one code.</summary>
    public static IReadOnlyList<HealthReportFinding> Of(HealthReport report, string code) =>
        [.. report.Findings.Where(x => string.Equals(x.Code, code, StringComparison.Ordinal))];

    /// <summary>A sweep of the regular checks and, when asked, the deeper ones.</summary>
    public static Task<HealthReport> ReportAsync(HealthLiveHost host, bool deeper = false) =>
        host.ReportAsync(deeper ? new HealthReportRequest(HealthDeeperChecks.Run) : HealthReportRequest.Regular);

    /// <summary>
    /// Runs <paramref name="action" /> in a circuit-shaped scope of the host's own container - for the Schema
    /// screen's live tests, which call <c>ISchemaDataService</c> and the advisor's cache-only read beside a sweep.
    /// </summary>
    public static async Task<T> InScopeAsync<T>(HealthLiveHost host, Func<IServiceProvider, Task<T>> action)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(action);

        await using AsyncServiceScope circuit = host.Services.CreateAsyncScope();
        return await action(circuit.ServiceProvider);
    }
}
