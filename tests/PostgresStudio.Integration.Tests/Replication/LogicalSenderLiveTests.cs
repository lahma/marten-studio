using System.Collections;
using System.Reflection;

using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using PostgresStudio.Integration.Tests.Health;
using PostgresStudio.Integration.Tests.Versions;
using PostgresStudio.Services;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Replication;
using PostgresStudio.Services.Sources;

namespace PostgresStudio.Integration.Tests.Replication;

/// <summary>
/// W5-R2-fix B1 on a real server: two <c>pg_recvlogical</c> walsenders, one decoding another database and one decoding the
/// studio's own. The other database's is counted and never named - not in the facts' rows, not anywhere in what the
/// Replication page is handed, not in R03 - and neither is judged as a standby; this database's is listed, named through the
/// activity gate, and still not judged.
/// </summary>
public class LogicalSenderLiveTests
{
    private const string OtherName = "secret_other_cdc";
    private const string OtherSlot = "secret_other_slot";
    private const string ThisName = "this_db_cdc";

    private static readonly StudioScope Scope = new("default", "default", null);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [PostgresFact]
    [HealthQuiet("R03")]
    public async Task Another_databases_logical_sender_is_counted_never_named_and_never_judged()
    {
        await using ReplicationServer server = await ReplicationServer.StartAsync(ReplicationServer.DefaultImage, null, null, "wal_level=logical");
        string database = new NpgsqlConnectionStringBuilder(server.SuperuserConnectionString).Database!;

        await server.ExecuteAsync("create database other_db");
        await ReplicationServer.Execute(
            new NpgsqlConnectionStringBuilder(server.SuperuserConnectionString) { Database = "other_db" }.ConnectionString,
            "select pg_catalog.pg_create_logical_replication_slot('" + OtherSlot + "', 'test_decoding')");
        await server.ExecuteAsync("select pg_catalog.pg_create_logical_replication_slot('this_db_slot', 'test_decoding')");

        await StartReceiverAsync(server, "other_db", OtherName, OtherSlot, "/tmp/other.log");
        await StartReceiverAsync(server, database, ThisName, "this_db_slot", "/tmp/this.log");

        try
        {
            await ReplicationServer.UntilAsync(
                async () => await server.ScalarAsync<long>(
                    "select pg_catalog.count(*) from pg_catalog.pg_stat_replication where application_name in ('" + OtherName + "', '" + ThisName + "')") == 2,
                TimeSpan.FromSeconds(60),
                "both logical walsenders to stream");
        }
        catch (TimeoutException exception)
        {
            var log = await server.Container.ExecAsync(["sh", "-c", "cat /tmp/other.log /tmp/this.log"], Token);
            throw new TimeoutException(exception.Message + " pg_recvlogical said: " + log.Stdout + log.Stderr, exception);
        }

        // As a member of pg_read_all_stats, which reads every sender's state and lag: the names are the studio's to withhold.
        await using StudioHost host = server.Host(MatrixRole.Stats, TimeProvider.System);

        ReplicationFacts facts = await host.CircuitAsync("admin", async services =>
        {
            ResolvedSource resolved = await services.GetRequiredService<StudioSourceResolver>().ResolveAsync(Scope, null, Token);
            return await services.GetRequiredService<IReplicationFactsReader>().ReadAsync(resolved, Token);
        });

        StandbySnapshot standbys = facts.Standbys.Value!;
        standbys.OtherDatabases.Should().Be(1, "the other database's sender is counted");
        standbys.Total.Should().Be(1, "only this database's sender is listed");
        StandbyRow row = standbys.Rows.Should().ContainSingle().Subject;
        row.Physical.Should().BeFalse();
        row.ApplicationName.Should().Be(ThisName);
        standbys.Physical.Should().BeEmpty();
        Strings.Of(standbys).Should().NotContain(static x => x.Contains("secret_other", StringComparison.Ordinal), "the read returns no name of another database's sender");

        // What the page is handed: every string reachable from the view.
        ReplicationView view = await host.CircuitAsync("admin", services => services.GetRequiredService<IReplicationService>().GetAsync(Scope, Token));
        IReadOnlyList<string> shown = [.. Strings.Of(view)];

        shown.Should().Contain(ThisName, "this database's sender is named through the activity gate - the walk does find names");
        shown.Should().NotContain(static x => x.Contains("secret_other", StringComparison.Ordinal), "neither the sender nor the slot of another database is named");
        view.Standbys.Value!.OtherDatabases.Should().Be(1);
        view.Standbys.Value.Rows.Should().ContainSingle().Which.Row.Physical.Should().BeFalse();

        // R03: nothing judged, both counted, the other one never named.
        HealthReport report = await host.CircuitAsync("admin", services => services.GetRequiredService<IHealthService>().GetReportAsync(Scope, HealthReportRequest.Regular, Token));
        HealthCheckStatus status = report.Checks.Single(static x => x.Code == "R03");

        report.Findings.Should().NotContain(static x => x.Code == "R03");
        status.Outcome.Should().Be(HealthCheckOutcome.Checked);
        status.Note.Should().Be("No standby streams from this server. Not standbys, and not judged: 1 logical sender of this database and "
            + "1 logical sender of other databases on this server, never named.");
        Strings.Of(status).Should().NotContain(static x => x.Contains("secret_other", StringComparison.Ordinal));
    }

    /// <summary>Starts <c>pg_recvlogical</c> in the server's container, detached, decoding <paramref name="slot" /> to nowhere.</summary>
    private static async Task StartReceiverAsync(ReplicationServer server, string database, string applicationName, string slot, string log)
    {
        string command = "setsid pg_recvlogical -d 'dbname=" + database + " user=postgres application_name=" + applicationName + "' -S " + slot
            + " --start -f /dev/null </dev/null >" + log + " 2>&1 &";

        var result = await server.Container.ExecAsync(["sh", "-c", command], Token);
        result.ExitCode.Should().Be(0, result.Stderr);
    }

    /// <summary>Every string reachable from an object of the studio's own types: properties, collections, nested records.</summary>
    private static class Strings
    {
        private static readonly Assembly Studio = typeof(ReplicationView).Assembly;

        public static IEnumerable<string> Of(object? root)
        {
            HashSet<object> seen = new(ReferenceEqualityComparer.Instance);
            Stack<(object Value, int Depth)> pending = new();

            if (root is not null)
            {
                pending.Push((root, 0));
            }

            while (pending.Count > 0)
            {
                (object value, int depth) = pending.Pop();
                Type type = value.GetType();

                if (value is string text)
                {
                    yield return text;
                    continue;
                }

                if (depth > 16 || type.IsPrimitive || type.IsEnum || value is Exception or Type or Delegate or MemberInfo
                    || (!type.IsValueType && !seen.Add(value)))
                {
                    continue;
                }

                if (value is IEnumerable items)
                {
                    foreach (object? item in items)
                    {
                        if (item is not null)
                        {
                            pending.Push((item, depth + 1));
                        }
                    }

                    continue;
                }

                bool tuple = type.IsGenericType && type.FullName!.StartsWith("System.ValueTuple", StringComparison.Ordinal);
                bool pair = type.IsGenericType && type.GetGenericTypeDefinition() == typeof(KeyValuePair<,>);

                if (type.Assembly != Studio && !tuple && !pair)
                {
                    continue;
                }

                foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if (property.GetIndexParameters().Length > 0)
                    {
                        continue;
                    }

                    if (Read(property, value) is { } next)
                    {
                        pending.Push((next, depth + 1));
                    }
                }

                foreach (FieldInfo field in tuple ? type.GetFields() : [])
                {
                    if (field.GetValue(value) is { } next)
                    {
                        pending.Push((next, depth + 1));
                    }
                }
            }
        }

        /// <summary>A property's value, or nothing when its getter throws - a getter that throws hands the page no string.</summary>
        private static object? Read(PropertyInfo property, object value)
        {
            try
            {
                return property.GetValue(value);
            }
            catch (TargetInvocationException)
            {
                return null;
            }
        }
    }
}
