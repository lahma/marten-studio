using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Database;
using PostgresStudio.Services.Server;

namespace PostgresStudio.Integration.Tests.Versions;

/// <summary>
/// 1.0-PG18 on the version matrix: what PostgreSQL 18 adds is read where the server has it and absent, never empty or an
/// error, where it does not - a virtual generated column, a temporal key and a <c>PERIOD</c> foreign key, and the Sessions
/// page's per-backend I/O.
/// </summary>
/// <remarks>
/// Each test makes a database of its own (<see cref="MatrixServer.CreateDatabaseAsync" />), so the seeded one the rest of the
/// matrix reads - and its no-DDL fingerprint - is untouched. The statements counters of extension 1.12 and the sessions
/// snapshot's I/O are held to the gate on every leg by <see cref="MatrixReads" />.
/// </remarks>
public class Pg18SurfacingLiveTests(PostgresVersionMatrix matrix)
{
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(5);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// <c>attgenerated</c> is <c>s</c> for a stored column on every release and <c>v</c> for a virtual one from 18; below 18
    /// the syntax itself is refused, which is what makes the gate a gate.
    /// </summary>
    [PostgresTheory]
    [MemberData(nameof(PostgresVersionMatrix.All), MemberType = typeof(PostgresVersionMatrix))]
    public async Task A_generated_column_is_read_as_stored_or_virtual_by_the_release_that_has_them(int major)
    {
        MatrixServer server = await matrix.ServerAsync(major);
        server.SkipIfUnavailable();

        string database = await server.CreateDatabaseAsync("pg18_generated", Token);
        await server.ExecuteAsync("create table public.gen (a int, stored_col int generated always as (a * 3) stored)", database, Token);

        bool virtualColumns = PostgresFeatures.Has(PostgresFeature.VirtualGeneratedColumns, server.VersionNumber);

        if (virtualColumns)
        {
            await server.ExecuteAsync("alter table public.gen add column virtual_col int generated always as (a * 2) virtual", database, Token);
        }
        else
        {
            PostgresException refused = await Assert.ThrowsAsync<PostgresException>(
                () => server.ExecuteAsync("alter table public.gen add column virtual_col int generated always as (a * 2) virtual", database, Token));
            refused.SqlState.Should().Be(PostgresErrorCodes.SyntaxError, "PostgreSQL " + server.ServerVersion + " has no virtual generated columns");
        }

        await using MatrixHost host = await MatrixHost.CreateAsync(server, MatrixRole.App, database);
        CatalogRelationDetail? detail = null;

        Exception? failure = await host.RunAsync(
            new MatrixRead("pg18: generated columns", true, async (x, t) =>
                detail = await DatabaseCatalogQueries.ReadRelationDetailAsync(x.Connection, x.Transaction, "public", "gen", x.Timeout, t)),
            LockTimeout);

        failure.Should().BeNull(failure?.Message);
        detail.Should().NotBeNull();
        detail!.Columns.Select(static x => (x.Name, x.Generated)).Should().Equal(
            virtualColumns
                ? [("a", string.Empty), ("stored_col", "s"), ("virtual_col", "v")]
                : [("a", string.Empty), ("stored_col", "s")]);
    }

    /// <summary>
    /// A <c>WITHOUT OVERLAPS</c> primary key and a <c>PERIOD</c> foreign key are read as temporal from 18 - by the one column,
    /// <c>pg_constraint.conperiod</c>, in the browser's two reads and the relationship read - and an ordinary key is never one.
    /// </summary>
    [PostgresTheory]
    [MemberData(nameof(PostgresVersionMatrix.All), MemberType = typeof(PostgresVersionMatrix))]
    public async Task A_temporal_key_and_a_period_foreign_key_are_read_as_such_from_18_and_an_ordinary_key_never(int major)
    {
        MatrixServer server = await matrix.ServerAsync(major);
        server.SkipIfUnavailable();

        bool temporal = PostgresFeatures.Has(PostgresFeature.TemporalConstraints, server.VersionNumber);
        string database = await server.CreateDatabaseAsync("pg18_temporal", Token);

        if (temporal)
        {
            await server.ExecuteAsync(
                "create extension btree_gist; "
                + "create table public.room (id int, valid_at daterange, primary key (id, valid_at without overlaps)); "
                + "create table public.booking (id int primary key, room_id int, valid_at daterange, "
                + "foreign key (room_id, period valid_at) references public.room (id, period valid_at)); "
                + "create table public.guest (id int primary key); "
                + "alter table public.booking add column guest_id int references public.guest (id)",
                database,
                Token);
        }
        else
        {
            await server.ExecuteAsync(
                "create table public.room (id int, valid_at daterange, primary key (id)); "
                + "create table public.booking (id int primary key, room_id int references public.room (id), valid_at daterange); "
                + "create table public.guest (id int primary key); "
                + "alter table public.booking add column guest_id int references public.guest (id)",
                database,
                Token);
        }

        await using MatrixHost host = await MatrixHost.CreateAsync(server, MatrixRole.App, database);
        CatalogRelationDetail? room = null;
        CatalogList<CatalogForeignKey>? keys = null;
        PhysicalForeignKeyRead? drawn = null;

        Exception? failure = await host.RunAsync(
            new MatrixRead("pg18: temporal keys", true, async (x, t) =>
            {
                room = await DatabaseCatalogQueries.ReadRelationDetailAsync(x.Connection, x.Transaction, "public", "room", x.Timeout, t);
                keys = await DatabaseCatalogQueries.ReadForeignKeysAsync(x.Connection, x.Transaction, ["public"], "public", "booking", 100, x.Timeout, t);
                drawn = await RelationshipQueries.ReadForeignKeysTouchingAsync(x.Connection, x.Transaction, ["public"], 100, x.Timeout, t);
            }),
            LockTimeout);

        failure.Should().BeNull(failure?.Message);

        room!.Constraints.Should().ContainSingle(static x => x.Kind == "p").Which.Temporal.Should().Be(temporal);
        room.Constraints.Should().NotContain(static x => x.Temporal && x.Kind != "p", "nothing but the key was declared WITHOUT OVERLAPS");

        keys!.Items.Where(static x => x.Table == "booking" && x.LinkedTable == "room").Should().ContainSingle().Which.Period.Should().Be(temporal);
        keys.Items.Where(static x => x.LinkedTable == "guest").Should().ContainSingle().Which.Period.Should().BeFalse("an ordinary foreign key is no period");

        drawn!.Keys.Where(static x => x.Table == "booking" && x.LinkedTable == "room").Should().ContainSingle().Which.Period.Should().Be(temporal);
        drawn.Keys.Where(static x => x.LinkedTable == "guest").Should().ContainSingle().Which.Period.Should().BeFalse();
    }

    /// <summary>
    /// The sessions snapshot reads every session's I/O in <b>one</b> statement from 18, and reads nothing below it. The function
    /// takes no role's leave (measured on 18.6 and 19rc1: a role in no monitoring role reads another role's backend, as
    /// <c>pg_stat_io</c> is readable by everyone), so what keeps it from a visitor is the activity gate the page puts it behind.
    /// </summary>
    [PostgresTheory]
    [MemberData(nameof(PostgresVersionMatrix.All), MemberType = typeof(PostgresVersionMatrix))]
    public async Task The_sessions_snapshot_reads_each_sessions_io_in_one_statement_from_18(int major)
    {
        MatrixServer server = await matrix.ServerAsync(major);
        server.SkipIfUnavailable();

        bool backendIo = PostgresFeatures.Has(PostgresFeature.BackendIo, server.VersionNumber);
        string database = await server.CreateDatabaseAsync("pg18_backend_io", Token);
        await server.ExecuteAsync("create table public.busy (id int primary key, n int); insert into public.busy select g, g from pg_catalog.generate_series(1, 5000) g; alter table public.busy owner to app", database, Token);

        // Two sessions of the application's own role, each of which has read the table, so each has I/O to show. Their statistics are
        // flushed by the server on its own clock; the test asks for the flush instead of waiting for it.
        await using NpgsqlConnection first = await server.OpenAsync(MatrixRole.App, database, Token);
        await using NpgsqlConnection second = await server.OpenAsync(MatrixRole.App, database, Token);

        foreach (NpgsqlConnection session in new[] { first, second })
        {
            await using NpgsqlCommand read = new("select sum(n) from public.busy", session);
            await read.ExecuteScalarAsync(Token);

            // Statistics are flushed when a transaction ends and a second has passed; the next statement's end flushes them now.
            if (PostgresFeatures.Has(PostgresFeature.ForceNextFlush, server.VersionNumber))
            {
                await using NpgsqlCommand flush = new("select pg_catalog.pg_stat_force_next_flush()", session);
                await flush.ExecuteScalarAsync(Token);
            }
        }

        // As the application's own role: its two sessions are its own to read into.
        await using (MatrixHost own = await MatrixHost.CreateAsync(server, MatrixRole.App, database))
        {
            SessionSnapshot snapshot = await own.Services.GetRequiredService<ISessionActivityReader>().ReadAsync(own.Resolved, Token);

            if (!backendIo)
            {
                snapshot.BackendIo.Should().BeNull("PostgreSQL " + server.ServerVersion + " has no pg_stat_get_backend_io");
                snapshot.Statements.Should().NotContain(static x => x.Label == "each session's I/O");
            }
            else
            {
                snapshot.Sessions.Select(static x => x.ProcessId).Should().Contain([first.ProcessID, second.ProcessID]);
                snapshot.BackendIo.Should().NotBeNull();
                snapshot.BackendIo!.Keys.Should().Contain([first.ProcessID, second.ProcessID], "its own role's sessions are readable");
                snapshot.BackendIo[first.ProcessID].Hits.Should().BePositive("the session read a table through shared buffers");
                snapshot.Statements.Count(static x => x.Label == "each session's I/O").Should().Be(1, "one statement for every session, never one a row");
            }
        }

        // As a role with no monitoring role: the sessions are another role's. The server answers for them all the same.
        await using MatrixHost stranger = await MatrixHost.CreateAsync(server, MatrixRole.Probe, database);
        SessionSnapshot strangers = await stranger.Services.GetRequiredService<ISessionActivityReader>().ReadAsync(stranger.Resolved, Token);

        if (!backendIo)
        {
            strangers.BackendIo.Should().BeNull();
        }
        else
        {
            strangers.BackendIo.Should().NotBeNull();
            strangers.BackendIo!.Keys.Should().Contain([first.ProcessID, second.ProcessID], "the function checks no role: the activity gate is the studio's");
            strangers.Statements.Count(static x => x.Label == "each session's I/O").Should().Be(1);
        }
    }
}
