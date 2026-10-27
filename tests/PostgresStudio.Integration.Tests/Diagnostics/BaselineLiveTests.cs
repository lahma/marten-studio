using System.Globalization;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using PostgresStudio.Integration.Tests.Versions;

namespace PostgresStudio.Integration.Tests.Diagnostics;

/// <summary>
/// Issue #31's three additions to the diagnostics snapshot, read live on PostgreSQL 14 to 18 and on 19's release candidate as a
/// preview: the write-ahead log's counters (<c>pg_stat_wal</c>), whether data checksums are on and what they counted, and each
/// table's all-visible ratio (<c>relallvisible / relpages</c>). Each is held to the catalog's own answer on the same server, so
/// a read that returned something plausible and wrong fails.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why every release.</b> <c>pg_stat_wal</c> exists from 14 and lost its write and sync counters on 18 (the section carries
/// none, so it reads alike), <c>data_checksums</c> is on by default only from 18, so the matrix holds both of its branches, and
/// <c>checksum_failures</c> is null where checksums are off.
/// </para>
/// <para>
/// A database of the test's own, with autovacuum off for its tables, so the ratio the test compares with is the one its
/// <c>VACUUM</c> wrote and no worker moves it between the two reads. The preview leg reports and never fails a run, as
/// <c>VersionMatrixLiveTests</c> does.
/// </para>
/// <para>
/// <b>The load commits synchronously.</b> The matrix servers run with <c>synchronous_commit = off</c> (Testcontainers' fast
/// defaults), and with it a committed row cannot be hinted as committed until the WAL writer has flushed its commit record,
/// which can be a moment after the commit returns. A <c>VACUUM</c> marks a page all-visible only when every row on it is hinted,
/// so one that ran at once set no page, whatever its horizon (measured on 14 to 18: 109 pages, none all-visible, on a second
/// and a third <c>VACUUM</c> too, run within milliseconds). The load's own commit flushes with it set.
/// </para>
/// </remarks>
public class BaselineLiveTests(PostgresVersionMatrix matrix)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [PostgresTheory]
    [MemberData(nameof(PostgresVersionMatrix.All), MemberType = typeof(PostgresVersionMatrix))]
    public async Task The_baseline_additions_read_on_every_release_and_agree_with_the_catalog(int major)
    {
        MatrixServer server = await matrix.ServerAsync(major);
        server.SkipIfUnavailable();

        string database = await server.CreateDatabaseAsync("ms_baseline", Token);

        // A table whose pages a VACUUM marks all-visible, and a partitioned one, whose ratio is its leaves' together.
        await server.ExecuteAsync(
            "set synchronous_commit = on; "
            + "create table public.events (id bigint primary key, note text) with (autovacuum_enabled = false); "
            + "insert into public.events select g, 'n' || g from pg_catalog.generate_series(1, 20000) g; "
            + "create table public.parts (k int not null, v int) partition by list (k); "
            + "create table public.parts_1 partition of public.parts for values in (1) with (autovacuum_enabled = false); "
            + "create table public.parts_2 partition of public.parts for values in (2) with (autovacuum_enabled = false); "
            + "insert into public.parts select 1 + g % 2, g from pg_catalog.generate_series(1, 20000) g;",
            database,
            Token);
        await server.ExecuteAsync("vacuum public.events", database, Token);
        await server.ExecuteAsync("vacuum public.parts", database, Token);

        await using MatrixHost host = await MatrixHost.CreateAsync(server, MatrixRole.App, database);
        PostgresDiagnosticsSnapshot snapshot = await host.Services.GetRequiredService<IPostgresDiagnostics>().GetSnapshotAsync(
            new PostgresDatabaseRef("default", ""),
            new PostgresDiagnosticsRequest { Findings = false, Storage = true, Settings = true, Wal = true },
            Token);

        await using NpgsqlConnection catalog = await server.OpenAsync(MatrixRole.App, database, Token);
        string checksums = (string) (await ScalarAsync(catalog, "show data_checksums"))!;
        double? events = await RatioAsync(catalog, "select case when relpages > 0 then relallvisible::float8 / relpages end from pg_catalog.pg_class where oid = 'public.events'::regclass");
        double? parts = await RatioAsync(
            catalog,
            "select case when sum(relpages) > 0 then sum(relallvisible)::float8 / sum(relpages) end from pg_catalog.pg_class "
            + "where oid in (select inhrelid from pg_catalog.pg_inherits where inhparent = 'public.parts'::regclass)");

        List<string> problems = [];

        // ---- Wal ------------------------------------------------------------------------------------------------------
        PostgresWalSection? wal = snapshot.Wal;

        if (wal is null)
        {
            problems.Add("Wal was asked for and is null.");
        }
        else if (wal.Outcome != PostgresReadOutcome.Read)
        {
            problems.Add("Wal is " + wal.Outcome + ": " + wal.Reason);
        }
        else
        {
            if (wal.Records is not > 0)
            {
                problems.Add("Wal.Records is " + Show(wal.Records) + ": the matrix server has written WAL since it started.");
            }

            if (wal.FullPageImages is not >= 0)
            {
                problems.Add("Wal.FullPageImages is " + Show(wal.FullPageImages) + ".");
            }

            if (wal.Bytes is not > 0)
            {
                problems.Add("Wal.Bytes is " + Show(wal.Bytes) + ".");
            }

            if (wal.Bytes < wal.Records)
            {
                problems.Add("Wal.Bytes " + Show(wal.Bytes) + " is fewer than its Records " + Show(wal.Records) + ": a record is at least a byte.");
            }

            if (wal.StatsReset is null)
            {
                problems.Add("Wal.StatsReset is null: pg_stat_wal.stats_reset is set when the statistics begin.");
            }
        }

        // ---- Settings: the checksum state ----------------------------------------------------------------------------------
        PostgresSettingsSection? settings = snapshot.Settings;

        if (settings is null || settings.Outcome != PostgresReadOutcome.Read)
        {
            problems.Add("Settings did not read: " + settings?.Outcome + " " + settings?.Reason);
        }
        else
        {
            bool on = checksums == "on";

            if (settings.DataChecksums != on)
            {
                problems.Add("Settings.DataChecksums is " + Show(settings.DataChecksums) + " and the server says data_checksums = " + checksums + ".");
            }

            long? failures = on ? 0 : null;

            if (settings.ChecksumFailures != failures)
            {
                problems.Add("Settings.ChecksumFailures is " + Show(settings.ChecksumFailures) + ", which should be "
                    + (on ? "a count of nought: checksums are on and nothing has failed one" : "null: checksums are off, and the server counts none") + ".");
            }

            if (settings.LastChecksumFailure is not null)
            {
                problems.Add("Settings.LastChecksumFailure is " + settings.LastChecksumFailure + " on a server that has failed no checksum.");
            }
        }

        // ---- Storage: the all-visible ratio ---------------------------------------------------------------------------
        PostgresStorageSection? storage = snapshot.Storage;

        if (storage is null || storage.Outcome != PostgresReadOutcome.Read)
        {
            problems.Add("Storage did not read: " + storage?.Outcome + " " + storage?.Reason);
        }
        else
        {
            foreach (PostgresTableStorage table in storage.LargestTables.Where(static x => x.AllVisibleRatio is < 0 or > 1))
            {
                problems.Add(table.Schema + "." + table.Name + " has an all-visible ratio of " + table.AllVisibleRatio + ", outside 0 to 1.");
            }

            Ratio(problems, storage, "events", events);
            Ratio(problems, storage, "parts", parts);

            if (events is not > 0)
            {
                problems.Add("The catalog's own all-visible ratio of public.events is " + Show(events) + ": the VACUUM marked no page all-visible.");
            }

            if (parts is not > 0)
            {
                problems.Add("The catalog's own all-visible ratio of public.parts's partitions is " + Show(parts) + ": the VACUUM marked no page all-visible.");
            }
        }

        // ---- The JSON ------------------------------------------------------------------------------------------------
        using JsonDocument json = JsonDocument.Parse(snapshot.ToJson());

        if (json.RootElement.GetProperty("schemaVersion").GetInt32() != 1)
        {
            problems.Add("schemaVersion moved: the additions keep 1.");
        }

        if (json.RootElement.GetProperty("wal").ValueKind != JsonValueKind.Object
            || json.RootElement.GetProperty("settings").GetProperty("dataChecksums").ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            problems.Add("The JSON lacks wal, or settings.dataChecksums is not a boolean.");
        }

        Report(server, problems);
    }

    /// <summary>The table's ratio is the catalog's, to the last digit: both are <c>relallvisible / relpages</c> in a double.</summary>
    private static void Ratio(List<string> problems, PostgresStorageSection storage, string table, double? catalog)
    {
        PostgresTableStorage? row = storage.LargestTables.FirstOrDefault(x => x.Schema == "public" && x.Name == table);

        if (row is null)
        {
            problems.Add("public." + table + " is not among the largest tables.");
        }
        else if (row.AllVisibleRatio != catalog)
        {
            problems.Add("public." + table + " has an all-visible ratio of " + Show(row.AllVisibleRatio) + "; the catalog's relallvisible / relpages is " + Show(catalog) + ".");
        }
    }

    private static async Task<object?> ScalarAsync(NpgsqlConnection connection, string sql)
    {
        await using NpgsqlCommand command = new(sql, connection);
        return await command.ExecuteScalarAsync(Token);
    }

    private static async Task<double?> RatioAsync(NpgsqlConnection connection, string sql) =>
        await ScalarAsync(connection, sql) is double ratio ? ratio : null;

    private static string Show<T>(T? value)
        where T : struct => value is { } present ? Convert.ToString(present, CultureInfo.InvariantCulture)! : "null";

    /// <summary>Fails with every problem - or, on the preview leg, reports them and skips (it never fails a run).</summary>
    private static void Report(MatrixServer server, List<string> problems)
    {
        if (problems.Count == 0)
        {
            return;
        }

        string text = "PostgreSQL " + server.ServerVersion + " (" + server.Version.Image + "):\n" + string.Join("\n", problems);

        if (server.Version.Preview)
        {
            TestContext.Current.TestOutputHelper?.WriteLine("Preview leg findings - {0}", text);
            Assert.Skip("Preview leg (" + server.Version.Image + "), reported and not failed: " + text);
        }

        problems.Should().BeEmpty(text);
    }
}
