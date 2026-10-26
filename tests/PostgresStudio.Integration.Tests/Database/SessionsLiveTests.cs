using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using PostgresStudio.Services;
using PostgresStudio.Services.Database;

namespace PostgresStudio.Integration.Tests.Database;

/// <summary>
/// Database › Sessions on a plain database: the summary anyone the source admits may see, and the details behind
/// <c>ViewDatabaseActivity</c> and the write policy - against two sessions of the test's own, one holding a lock and one
/// waiting for it.
/// </summary>
/// <remarks>
/// The database is the test's own, so every session in it is one the test opened or the studio's own read. The waiter's
/// statement is left running while the studio reads, and cancelled - by rolling back the holder - afterwards; nothing is
/// waited for with a delay, only on <c>pg_locks</c> saying the waiter waits.
/// </remarks>
public class SessionsLiveTests(PostgresFixture postgres)
{
    private const string Application = "ps_sessions_waiter";

    private static readonly StudioScope Scope = new("default", "default", null);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [PostgresFact]
    public async Task The_summary_counts_the_sessions_and_the_wait_and_the_details_name_the_blocker_only_past_the_activity_gate()
    {
        await using OwnedDatabases owned = await OwnedDatabases.CreateAsync(postgres, ["ps_sessions"], Token);
        await OwnedDatabases.ExecuteAsync(owned[0], "create table public.jobs (id int primary key); insert into public.jobs values (1)", Token);

        await using NpgsqlConnection holder = new(ServerSnapshot.Unpooled(owned[0]));
        await holder.OpenAsync(Token);
        await using NpgsqlTransaction held = await holder.BeginTransactionAsync(Token);
        await using (NpgsqlCommand take = new("lock table public.jobs in access exclusive mode", holder, held))
        {
            await take.ExecuteNonQueryAsync(Token);
        }

        await using NpgsqlConnection waiter = new(new NpgsqlConnectionStringBuilder(ServerSnapshot.Unpooled(owned[0])) { ApplicationName = Application }.ConnectionString);
        await waiter.OpenAsync(Token);
        await using NpgsqlCommand blocked = new("select count(*) from public.jobs", waiter) { CommandTimeout = 120 };
        Task<object?> waiting = blocked.ExecuteScalarAsync(Token);

        await WaitUntilWaitingAsync(owned[0]);

        await using StudioHost host = StudioHost.Create(
            services => services.AddSingleton(_ => NpgsqlDataSource.Create(owned[0])),
            static o => o.Capabilities = PostgresStudioCapabilities.All(),
            writePolicy: static (resource, user) => user.Identity?.Name == "admin");

        try
        {
            await using (StudioCircuit viewer = host.Circuit("viewer"))
            {
                ISessionActivityService sessions = viewer.Get<ISessionActivityService>();

                SessionActivitySummary summary = await sessions.GetSummaryAsync(Scope, Token);

                summary.Refusal.Should().Be(DatabaseRefusal.None, summary.Reason);
                summary.SessionCount.Should().BeGreaterThanOrEqualTo(2, "the holder and the waiter are sessions of this database");
                summary.Waiting.Should().BeGreaterThanOrEqualTo(1, "the waiter waits on the holder's lock");
                summary.Sessions.Should().Contain(static x => x.BlockedByCount > 0);
                summary.LockCount.Should().BePositive();
                summary.Details.Refusal.Should().Be(DatabaseRefusal.WritePolicy, "the summary says what the details would need");

                SessionActivityDetails refused = await sessions.GetDetailsAsync(Scope, Token);
                refused.Refusal.Should().Be(DatabaseRefusal.WritePolicy, refused.Reason);
                refused.Sessions.Should().BeEmpty("other people's sessions are behind the activity gate");
            }

            await using (StudioCircuit admin = host.Circuit("admin"))
            {
                SessionActivityDetails details = await admin.Get<ISessionActivityService>().GetDetailsAsync(Scope, Token);

                details.Refusal.Should().Be(DatabaseRefusal.None, details.Reason);
                SessionDetailRow row = details.Sessions.Should().ContainSingle(static x => x.Application == Application).Subject;
                row.BlockedBy.Should().Contain(holder.ProcessID, "the details name the session it waits for");
                row.Statement.Text.Should().Contain("select count(*) from public.jobs");
                details.Locks.Should().Contain(x => x.ProcessId == holder.ProcessID && x.Granted && x.Relation == "public.jobs");
            }

            host.Calls.All.Should().Contain(static x => x.Policy == StudioHost.WritePolicy
                && x.Resource == new PostgresStudioResource("default", "default", nameof(PostgresStudioCapabilities.ViewDatabaseActivity)));
            host.Ring.GetLatest().Where(static x => x.Action == "Read database sessions").Should().Contain(static x => x.User == "viewer" && !x.Succeeded,
                "a refusal of the details is audited");
        }
        finally
        {
            await held.RollbackAsync(Token);
            (await waiting).Should().Be(1L, "the waiter ran once the holder let go");
        }
    }

    [PostgresFact]
    public async Task Without_ViewDatabaseActivity_the_summary_is_still_shown_and_the_details_are_refused()
    {
        await using OwnedDatabases owned = await OwnedDatabases.CreateAsync(postgres, ["ps_sessions_off"], Token);

        // A session of the application's: the summary does not count the studio's own read.
        await using NpgsqlConnection application = new(ServerSnapshot.Unpooled(owned[0]));
        await application.OpenAsync(Token);

        await using StudioHost host = StudioHost.Create(services => services.AddSingleton(_ => NpgsqlDataSource.Create(owned[0])));
        await using StudioCircuit circuit = host.Circuit();
        ISessionActivityService sessions = circuit.Get<ISessionActivityService>();

        SessionActivitySummary summary = await sessions.GetSummaryAsync(Scope, Token);
        summary.Refusal.Should().Be(DatabaseRefusal.None, summary.Reason);
        summary.SessionCount.Should().Be(1, "the application's one session, and not the studio's own read");

        SessionActivityDetails details = await sessions.GetDetailsAsync(Scope, Token);
        details.Refusal.Should().Be(DatabaseRefusal.CapabilityOff, details.Reason);
        details.Reason.Should().Contain("ViewDatabaseActivity");
    }

    /// <summary>Waits, on the condition and never on the clock, until the waiter's lock request is in <c>pg_locks</c> ungranted.</summary>
    private static async Task WaitUntilWaitingAsync(string connectionString)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        using PeriodicTimer timer = new(TimeSpan.FromMilliseconds(50));

        await using NpgsqlConnection probe = new(ServerSnapshot.Unpooled(connectionString));
        await probe.OpenAsync(deadline.Token);

        do
        {
            await using NpgsqlCommand command = new(
                "select exists (select 1 from pg_catalog.pg_locks l join pg_catalog.pg_stat_activity a on a.pid = l.pid "
                + "where not l.granted and a.application_name = @application)",
                probe);
            command.Parameters.AddWithValue("application", Application);

            if ((bool) (await command.ExecuteScalarAsync(deadline.Token))!)
            {
                return;
            }
        }
        while (await timer.WaitForNextTickAsync(deadline.Token));
    }
}
