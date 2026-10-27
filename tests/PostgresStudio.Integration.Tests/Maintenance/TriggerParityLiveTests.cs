using System.Globalization;

using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using PostgresStudio.Integration.Tests.Health;
using PostgresStudio.Internal.Sql;
using PostgresStudio.Services;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Health.Checks;
using PostgresStudio.Services.Schema;
using PostgresStudio.Services.Settings;
using PostgresStudio.Services.Sources;

namespace PostgresStudio.Integration.Tests.Maintenance;

/// <summary>
/// The triggers T07, T08 and the Maintenance page judge by are computed in SQL (<see cref="HealthTableQueries.TablesSql" />);
/// <see cref="Autovacuum.Triggers" /> is the C# mirror the unit tests hold to B0's numbers. This holds the two equal on a real
/// server, table by table, over every case the formula has (W5-V1 review): PostgreSQL 18's cap as a setting, as a storage
/// parameter and switched off by one at <c>-1</c>; a partial unfrozen share; an insert threshold of <c>-1</c>, which switches
/// the insert trigger off; and a table never analysed, whose <c>reltuples = -1</c> counts as nought.
/// </summary>
/// <remarks>
/// The inputs are read here on their own - <c>pg_class</c>, the table's <c>reloptions</c> and the server's settings - and
/// combined as Postgres documents (the table's storage parameter over the setting); the triggers are the statement's.
/// Autovacuum is switched off for each table, which the formula does not read, so nothing moves the numbers while they are read.
/// </remarks>
public class TriggerParityLiveTests
{
    private const double ServerCap = 2_000;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [PostgresTheory]
    [InlineData(17)]
    [InlineData(18)]
    public async Task The_statements_triggers_equal_Autovacuum_Triggers_over_the_same_inputs(int major)
    {
        bool capped = major >= 18;
        await using AutovacuumServer server = capped
            ? await AutovacuumServer.StartAsync(major, "autovacuum_vacuum_max_threshold=" + ServerCap.ToString(CultureInfo.InvariantCulture))
            : await AutovacuumServer.StartAsync(major);

        // The load commits synchronously (the statements share one connection): the Testcontainers server runs with
        // synchronous_commit = off, and a committed row is hinted only once the WAL writer has flushed its commit record, which
        // the VACUUM (FREEZE) below can precede - and a page with a row not hinted committed is not frozen (BaselineLiveTests).
        List<string> statements =
        [
            "set synchronous_commit = on",
            "create schema ps_parity",
            "create table ps_parity.plain (id int, note text) with (autovacuum_enabled = false)",
            "create table ps_parity.partial (id int, note text) with (autovacuum_enabled = false)",
            "create table ps_parity.no_insert (id int, note text) with (autovacuum_enabled = false, autovacuum_vacuum_insert_threshold = -1)",
            "create table ps_parity.tuned (id int, note text) with (autovacuum_enabled = false, autovacuum_vacuum_threshold = 10, "
            + "autovacuum_vacuum_scale_factor = 0.01, autovacuum_analyze_scale_factor = 0.02, autovacuum_vacuum_insert_scale_factor = 0.5)",
            "create table ps_parity.never (id int primary key, note text) with (autovacuum_enabled = false)",
            "insert into ps_parity.plain select g, 'p' || g from generate_series(1, 20000) g",
            "insert into ps_parity.partial select g, 'p' || g from generate_series(1, 10000) g",
            "insert into ps_parity.no_insert select g, 'n' || g from generate_series(1, 5000) g",
            "insert into ps_parity.tuned select g, 't' || g from generate_series(1, 30000) g",
            "insert into ps_parity.never select g, 'v' || g from generate_series(1, 3000) g",
        ];

        if (capped)
        {
            statements.Add("create table ps_parity.cap_param (id int, note text) with (autovacuum_enabled = false, autovacuum_vacuum_max_threshold = 100)");
            statements.Add("create table ps_parity.cap_off (id int, note text) with (autovacuum_enabled = false, autovacuum_vacuum_max_threshold = -1)");
            statements.Add("insert into ps_parity.cap_param select g, 'c' || g from generate_series(1, 20000) g");
            statements.Add("insert into ps_parity.cap_off select g, 'o' || g from generate_series(1, 20000) g");
        }

        await server.ExecuteAsync([.. statements]);

        // Frozen whole, then grown and analysed: the new pages are not all-frozen, the old ones are - a partial share on 18.
        await AutovacuumServer.UntilAsync(
            async () =>
            {
                await server.ExecuteAsync("vacuum (freeze) ps_parity.partial");
                return !capped || await server.ScalarAsync<bool>(
                    "select c.relpages > 0 and c.relallfrozen = c.relpages from pg_catalog.pg_class c where c.oid = 'ps_parity.partial'::regclass");
            },
            TimeSpan.FromSeconds(60),
            "VACUUM (FREEZE) to freeze every page of ps_parity.partial");

        await server.ExecuteAsync(
            "insert into ps_parity.partial select g, 'q' || g from generate_series(10001, 15000) g",
            "analyze ps_parity.plain",
            "analyze ps_parity.partial",
            "analyze ps_parity.no_insert",
            "analyze ps_parity.tuned");

        if (capped)
        {
            await server.ExecuteAsync("analyze ps_parity.cap_param", "analyze ps_parity.cap_off");
        }

        IReadOnlyList<TableFacts> tables = await TablesAsync(server);
        string[] names = capped
            ? ["plain", "partial", "no_insert", "tuned", "never", "cap_param", "cap_off"]
            : ["plain", "partial", "no_insert", "tuned", "never"];

        List<string> problems = [];

        foreach (string name in names)
        {
            TableFacts table = tables.Single(x => x.Table.Schema == "ps_parity" && x.Table.Table == name);
            AutovacuumTriggers expected = Autovacuum.Triggers(await InputsAsync(server, name, capped));

            Compare(problems, name, "vacuum", table.Row.Vacuum?.Trigger, expected.Vacuum);
            Compare(problems, name, "insert", table.Row.Insert?.Trigger, expected.Insert);
            Compare(problems, name, "analyze", table.Row.Analyze?.Trigger, expected.Analyze);
            Compare(problems, name, "unfrozen share", table.Row.UnfrozenShare, expected.UnfrozenShare);
        }

        problems.Should().BeEmpty(string.Join(Environment.NewLine, problems));

        // The cases are the ones they claim to be.
        TableFacts never = tables.Single(static x => x.Table.Table == "never");
        never.Row.EstimatedRows.Should().Be(-1, "never analysed, with indexes built while it was empty");
        tables.Single(static x => x.Table.Table == "no_insert").Row.Insert.Should().BeNull("an insert threshold of -1 switches the insert trigger off");

        if (capped)
        {
            tables.Single(static x => x.Table.Table == "plain").Row.Vacuum!.Trigger.Should().Be(ServerCap, "the server's cap holds 4,050 at 2,000");
            tables.Single(static x => x.Table.Table == "cap_param").Row.Vacuum!.Trigger.Should().Be(100, "the table's own cap is under the server's");
            tables.Single(static x => x.Table.Table == "cap_off").Row.Vacuum!.Trigger.Should().Be(4_050, "-1 switches the cap off for the table");
            tables.Single(static x => x.Table.Table == "partial").Row.UnfrozenShare.Should().BeInRange(0.01, 0.99, "a share, not all or nothing");
        }
    }

    private static void Compare(List<string> problems, string table, string what, double? actual, double? expected)
    {
        bool equal = (actual, expected) switch
        {
            (null, null) => true,
            ({ } a, { } e) => Math.Abs(a - e) <= 1e-6 * Math.Max(1, Math.Abs(e)),
            _ => false,
        };

        if (!equal)
        {
            problems.Add(table + " " + what + ": the statement says " + (actual?.ToString(CultureInfo.InvariantCulture) ?? "none")
                + ", Autovacuum.Triggers " + (expected?.ToString(CultureInfo.InvariantCulture) ?? "none"));
        }
    }

    /// <summary>What Postgres' formula takes, read on its own: the table's own storage parameters over the server's settings.</summary>
    private static async Task<AutovacuumInputs> InputsAsync(AutovacuumServer server, string table, bool capped)
    {
        await using NpgsqlConnection connection = await server.OpenAsync();
        await using NpgsqlCommand command = new(
            "select c.reltuples::float8, c.relpages::bigint, (pg_catalog.to_jsonb(c) ->> 'relallfrozen')::bigint, coalesce(c.reloptions, '{}'::text[]), "
            + "pg_catalog.current_setting('autovacuum_vacuum_threshold')::float8, pg_catalog.current_setting('autovacuum_vacuum_scale_factor')::float8, "
            + "pg_catalog.current_setting('autovacuum_vacuum_insert_threshold')::float8, pg_catalog.current_setting('autovacuum_vacuum_insert_scale_factor')::float8, "
            + "pg_catalog.current_setting('autovacuum_analyze_threshold')::float8, pg_catalog.current_setting('autovacuum_analyze_scale_factor')::float8, "
            + "(select s.setting::float8 from pg_catalog.pg_settings s where s.name = 'autovacuum_vacuum_max_threshold') "
            + "from pg_catalog.pg_class c where c.oid = ('ps_parity.' || @table)::regclass",
            connection);
        command.Parameters.AddWithValue("table", table);

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(Token);
        (await reader.ReadAsync(Token)).Should().BeTrue();

        IReadOnlyDictionary<string, string> own = StorageParameters.Parse(reader.GetFieldValue<string[]>(3));

        double Setting(string parameter, int ordinal) =>
            own.TryGetValue(parameter, out string? value) ? double.Parse(value, CultureInfo.InvariantCulture) : reader.GetDouble(ordinal);

        return new AutovacuumInputs
        {
            Reltuples = reader.GetDouble(0),
            RelPages = reader.GetInt64(1),
            RelAllFrozen = reader.IsDBNull(2) ? null : reader.GetInt64(2),
            VacuumThreshold = Setting("autovacuum_vacuum_threshold", 4),
            VacuumScaleFactor = Setting("autovacuum_vacuum_scale_factor", 5),
            InsertThreshold = Setting("autovacuum_vacuum_insert_threshold", 6),
            InsertScaleFactor = Setting("autovacuum_vacuum_insert_scale_factor", 7),
            AnalyzeThreshold = Setting("autovacuum_analyze_threshold", 8),
            AnalyzeScaleFactor = Setting("autovacuum_analyze_scale_factor", 9),
            VacuumMaxThreshold = !capped ? null
                : own.TryGetValue("autovacuum_vacuum_max_threshold", out string? cap) ? double.Parse(cap, CultureInfo.InvariantCulture)
                : reader.GetDouble(10),
        };
    }

    /// <summary>The table read, exactly as a sweep runs it, over a host of the server's <c>ps_parity</c> schema.</summary>
    internal static async Task<IReadOnlyList<TableFacts>> TablesAsync(AutovacuumServer server)
    {
        await using StudioHost host = server.Host("ps_parity", new ManualClock(DateTimeOffset.UtcNow));
        await using StudioCircuit circuit = host.Circuit();

        ResolvedSource resolved = await circuit.Get<StudioSourceResolver>().ResolveAsync(new StudioScope("default", "default", null), null, Token);
        await using HealthConnection connection = new(resolved.Target);

        HealthCheckContext context = new(
            resolved,
            host.Services.GetRequiredService<IStudioSettings>(),
            NoStudioRuntime.Instance,
            host.Services,
            new HealthSqlRunner(connection, new ReadOnlySqlOptions(), null),
            DateTimeOffset.UtcNow,
            new HealthSweepMemo(),
            Token);

        return await HealthTables.TablesAsync(context);
    }
}
