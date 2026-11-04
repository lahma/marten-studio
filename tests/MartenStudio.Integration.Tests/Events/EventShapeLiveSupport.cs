using Npgsql;

namespace MartenStudio.Integration.Tests.Events;

/// <summary>Raw SQL for arranging events the way a host's history leaves them: behind Marten's back, in the events table.</summary>
internal static class EventShapeLiveSupport
{
    /// <summary>The planted value: it must appear in no result, log line or page.</summary>
    public const string Sentinel = "SENTINEL-7f3a";

    /// <summary>The qualified <c>mt_events</c> of a fixture whose events live in <c>{schema}_events</c>.</summary>
    public static string EventsTable(string schema) => $"\"{schema}_events\".mt_events";

    public static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken token)
    {
        await using NpgsqlCommand command = new(sql, connection) { CommandTimeout = 120 };
        await command.ExecuteNonQueryAsync(token);
    }

    public static async Task<object?> ScalarAsync(NpgsqlConnection connection, string sql, CancellationToken token)
    {
        await using NpgsqlCommand command = new(sql, connection) { CommandTimeout = 120 };
        return await command.ExecuteScalarAsync(token);
    }

    public static async Task<List<long>> SequencesAsync(NpgsqlConnection connection, string schema, string type, CancellationToken token)
    {
        List<long> sequences = [];

        await using NpgsqlCommand command = new($"select seq_id from {EventsTable(schema)} where type = '{type}' order by seq_id", connection);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(token);

        while (await reader.ReadAsync(token))
        {
            sequences.Add(reader.GetInt64(0));
        }

        return sequences;
    }

    /// <summary>One event row in an existing stream, with the type, .NET type name and JSON the test says.</summary>
    public static Task InsertAsync(
        NpgsqlConnection connection,
        string schema,
        Guid stream,
        long version,
        string type,
        string? dotNetType,
        string json,
        string? tenant,
        CancellationToken token)
    {
        string tenantColumn = tenant is null ? string.Empty : ", tenant_id";
        string tenantValue = tenant is null ? string.Empty : $", '{tenant}'";
        string dotNet = dotNetType is null ? "null" : $"'{dotNetType}'";

        return ExecuteAsync(
            connection,
            $"insert into {EventsTable(schema)} (seq_id, id, stream_id, version, data, type, timestamp, mt_dotnet_type, is_archived{tenantColumn}) "
            + $"values (nextval('\"{schema}_events\".mt_events_sequence'), gen_random_uuid(), '{stream}', {version}, '{json}'::jsonb, '{type}', now(), {dotNet}, false{tenantValue})",
            token);
    }

    /// <summary>What <c>pg_class</c> says is in the fixture's two schemas: a change to it is DDL.</summary>
    public static async Task<string> ObjectsAsync(NpgsqlConnection connection, string schema, CancellationToken token)
    {
        await using NpgsqlCommand command = new(
            "select string_agg(n.nspname || '.' || c.relname || ':' || c.relkind::text || ':' || c.relnatts::text, ',' order by n.nspname, c.relname) "
            + "from pg_catalog.pg_class c join pg_catalog.pg_namespace n on n.oid = c.relnamespace where n.nspname like @schema",
            connection);
        command.Parameters.AddWithValue("schema", schema + "%");

        return (string) (await command.ExecuteScalarAsync(token))!;
    }
}
