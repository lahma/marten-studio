namespace PostgresStudio.Integration.Tests.Versions;

/// <summary>
/// Amazon Aurora PostgreSQL's diagnostic functions, as plain SQL functions on a vanilla server: what a test needs to see
/// the studio's Aurora wording and its Aurora reads without an AWS account.
/// </summary>
/// <remarks>
/// <para>
/// <b>B0's shim, verbatim</b> (<c>B0-findings.md</c>, "Aurora (A1-A7): the function signatures, for a test shim"), from the
/// AWS documentation of each function (fetched 2026-10-20). The types are B0's inferred ones where the documentation does
/// not state them, and the column set of <c>aurora_replica_status()</c> varies by Aurora release, so the studio reads it by
/// name (<c>to_jsonb</c>), never by position. <c>aurora_stat_statements(showtext)</c> is not shimmed: on 17 and later its
/// column list collides with <c>pg_stat_statements</c>' own, and AUR adds it when a read needs it.
/// </para>
/// <para>
/// <b>Never on a database a self-hosted test reads.</b> Defining <c>aurora_version()</c> trips the managed-platform
/// detector (<c>PostgresPlatform.Detect</c>, which looks the function up in <c>pg_proc</c>), and <c>pg_proc</c> is per
/// database - so <see cref="ApplyAsync" /> makes a database of its own and shims that, and every other test on the same
/// server still sees a server of somebody's own.
/// </para>
/// <para>
/// <b>In <c>pg_catalog</c>, as the superuser</b> (W5-R2-fix B3): the detector counts <c>aurora_version()</c> only there,
/// where no role short of a superuser may create a function, so a <c>public.aurora_version()</c> a database owner made does
/// not turn a server of somebody's own into an Aurora cluster and silence R02-R04.
/// </para>
/// </remarks>
public static class AuroraShim
{
    /// <summary>The database a shim lives in, unless the caller names another.</summary>
    public const string DefaultDatabase = "aurora_shim";

    /// <summary>What <c>aurora_version()</c> answers.</summary>
    public const string Version = "16.4.1";

    /// <summary>What <c>aurora_db_instance_identifier()</c> answers: the writer's identifier.</summary>
    public const string InstanceIdentifier = "studio-test-instance-1";

    // test shim: Aurora function signatures from AWS docs, B0 2026-10-20
    private const string ShimSql =
        """
        create function pg_catalog.aurora_version() returns varchar language sql as $$ select '16.4.1'::varchar $$;
        create function pg_catalog.aurora_db_instance_identifier() returns varchar language sql as $$ select 'studio-test-instance-1'::varchar $$;
        create function pg_catalog.aurora_replica_status(out server_id text, out session_id text, out durable_lsn bigint, out highest_lsn_rcvd bigint,
          out current_read_lsn bigint, out cur_replay_latency_in_usec bigint, out active_txns int, out is_current boolean,
          out last_transport_error int, out last_error_timestamp timestamptz, out last_update_timestamp timestamptz,
          out feedback_xmin bigint, out feedback_epoch int, out replica_lag_in_msec real, out log_stream_speed_in_kib_per_second real,
          out log_buffer_sequence_number bigint, out oldest_read_view_trx_id bigint, out oldest_read_view_lsn bigint,
          out pending_read_ios int, out read_ios bigint, out iops int, out cpu real) returns setof record language sql as $$
          values ('studio-test-instance-1', 'MASTER_SESSION_ID', 88492069::bigint, null::bigint, null::bigint, null::bigint, null::int, true, 0, null::timestamptz, null::timestamptz, null::bigint, null::int, null::real, 0::real, 0::bigint, null::bigint, null::bigint, 0, 0::bigint, null::int, null::real),
                 ('studio-test-instance-2', gen_random_uuid()::text, 88492069, 88492069, 88492060, 120, 1, false, 0, null, now(), null, null, 13, 2.5, 0, null, null, 0, 10, null, 1.5) $$;
        create function pg_catalog.aurora_stat_wait_type(out type_id int, out type_name text) returns setof record language sql as $$ values (1,'LWLock'),(3,'Lock'),(10,'IO') $$;
        create function pg_catalog.aurora_stat_wait_event(out type_id int, out event_id int, out event_name text) returns setof record language sql as $$ values (10,167772231,'XactSync'),(10,167772230,'WALWrite') $$;
        create function pg_catalog.aurora_stat_system_waits(out type_id int, out event_id int, out waits bigint, out wait_time bigint) returns setof record language sql as $$ values (10,167772231,187073::bigint,597041642::bigint) $$;
        create function pg_catalog.aurora_stat_backend_waits(pid int, out type_id int, out event_id int, out waits bigint, out wait_time bigint) returns setof record language sql as $$ values (10,167772231,4867::bigint,7331963::bigint) $$;
        create function pg_catalog.aurora_stat_get_db_commit_latency(database_oid oid) returns bigint language sql as $$ select 22011::bigint $$;
        create function pg_catalog.aurora_stat_dml_activity(database_oid oid, out select_count bigint, out select_latency_microsecs bigint,
          out insert_count bigint, out insert_latency_microsecs bigint, out update_count bigint, out update_latency_microsecs bigint,
          out delete_count bigint, out delete_latency_microsecs bigint) returns setof record language sql as $$ values (178957::bigint,66684115::bigint,171065::bigint,28876649::bigint,519538::bigint,1454579206167::bigint,1::bigint,53027::bigint) $$;
        """;

    /// <summary>
    /// A database of its own on <paramref name="server" />, owned by <c>app</c>, with the shim in its <c>pg_catalog</c>
    /// (created by the superuser, executable by every role, as no Aurora page says otherwise - A7).
    /// </summary>
    /// <returns>The shimmed database's name.</returns>
    public static async Task<string> ApplyAsync(MatrixServer server, string database = DefaultDatabase, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(server);

        await server.CreateDatabaseAsync(database, cancellationToken);
        await server.ExecuteAsync(ShimSql, database, cancellationToken);

        return database;
    }
}
