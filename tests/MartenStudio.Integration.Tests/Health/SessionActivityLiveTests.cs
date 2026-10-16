using Marten;

using MartenStudio.Internal.Sql;
using MartenStudio.SampleDomain;
using MartenStudio.SampleDomain.Events;
using MartenStudio.Services;
using MartenStudio.Services.Database;
using MartenStudio.Services.Health;
using MartenStudio.Services.Health.Checks;

using Microsoft.Extensions.DependencyInjection;

using Npgsql;

namespace MartenStudio.Integration.Tests.Health;

/// <summary>
/// The session checks and Database › Sessions against real sessions in a database of each test's own: an open
/// transaction on an <c>mt_streams</c> row and a session waiting behind it (C04, C06), a statement that keeps
/// running (C05), a withheld schema named in a running statement (acceptance 5), the details gated both ways
/// (acceptance 3), the first read audited once - and the healthy sample store quiet for all of them.
/// </summary>
public class SessionActivityLiveTests(PostgresFixture postgres)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static void Everything(MartenStudioOptions options) => options.Capabilities = MartenStudioCapabilities.All();

    [PostgresFact]
    [HealthFires("C04")]
    [HealthFires("C06")]
    public async Task A_transaction_left_open_on_an_mt_streams_row_fires_C04_and_C06_with_who_behind_the_gate()
    {
        string database = await ConnectionsLiveHost.CreateDatabaseAsync(postgres.ConnectionString, "health_c04_c06_fires");
        await using ConnectionsLiveHost open = await ConnectionsLiveHost.CreateAsync(database, studio: Everything);
        await using ConnectionsLiveHost closed = await ConnectionsLiveHost.CreateAsync(database);

        Guid stream = Guid.NewGuid();
        await using (IDocumentSession session = open.Store.LightweightSession())
        {
            session.Events.StartStream(stream, new OrderPlaced(stream, "acme", DateTimeOffset.UtcNow));
            await session.SaveChangesAsync(Token);
        }

        string streams = SqlIdentifier.Qualify(SampleStore.EventSchema, "mt_streams");

        // The holder takes the row and goes quiet inside its transaction; the waiter queues behind it.
        await using NpgsqlConnection holder = await open.OpenAsync();
        await using NpgsqlTransaction held = await holder.BeginTransactionAsync(Token);
        await using (NpgsqlCommand take = new($"update {streams} set version = version where id = @id", holder, held))
        {
            take.Parameters.AddWithValue("id", stream);
            await take.ExecuteNonQueryAsync(Token);
        }

        await using NpgsqlConnection waiter = await open.OpenAsync();
        // No client-side timeout: waiting longer than Npgsql's default thirty seconds is what this session is for.
        await using NpgsqlCommand blocked = new($"update {streams} set version = version where id = @id", waiter) { CommandTimeout = 0 };
        blocked.Parameters.AddWithValue("id", stream);
        Task waiting = blocked.ExecuteNonQueryAsync(Token);

        int holderPid = holder.ProcessID;
        int waiterPid = waiter.ProcessID;

        try
        {
            await LiveWait.UntilAsync(
                async () => await LiveWait.ScalarAsync<bool>(
                    database,
                    "select count(*) = 2 and bool_and(ok) from ("
                    + " select now() - state_change > interval '62 seconds' as ok from pg_stat_activity where pid = @holder"
                    + " union all"
                    + " select now() - state_change > interval '32 seconds' and wait_event_type = 'Lock' from pg_stat_activity where pid = @waiter) s",
                    ("holder", holderPid),
                    ("waiter", waiterPid)),
                TimeSpan.FromSeconds(120),
                "the holder to sit idle in its transaction for a minute, and the waiter to wait thirty seconds");

            HealthReport named = await open.ReportAsync();
            HealthReport unnamed = await closed.ReportAsync();

            // C04: the same finding for both, the session named only through the gate.
            HealthReportFinding idle = named.Findings.Should().ContainSingle(static x => x.Code == "C04").Subject;
            idle.Severity.Should().Be(HealthSeverity.Warning);
            idle.Evidence.Single(static x => x.Label == "Session 1 (longest)").Value.Should().StartWith("pid " + holderPid);
            idle.Evidence.Should().Contain(new HealthEvidence("Have written (hold a transaction id)", "1"));

            HealthReportFinding idleUnnamed = unnamed.Findings.Should().ContainSingle(static x => x.Code == "C04").Subject;
            idleUnnamed.Evidence.Single(static x => x.Label == "Session 1 (longest)").Value.Should().StartWith("withheld - ")
                .And.Contain("MartenStudioOptions.Capabilities.ViewDatabaseActivity");
            idleUnnamed.Evidence.Single(static x => x.Label == "Longest idle").Value.Should().EndWith("min", "an age is the database's");

            // C06: what is waited for, in Marten's terms; who waits and who holds it, only through the gate.
            HealthReportFinding wait = named.Findings.Should().ContainSingle(static x => x.Code == "C06").Subject;
            wait.Evidence.Single(static x => x.Label == "Waiting for").Value.Should().Contain("an mt_streams row (" + SampleStore.EventSchema + ")");
            wait.Evidence.Single(static x => x.Label == "Held up by").Value.Should().StartWith("1 session idle in transaction");
            wait.Evidence.Single(static x => x.Label == "Waiter 1 (longest)").Value.Should().Contain("pid " + waiterPid).And.Contain("blocked by pid " + holderPid);
            wait.Evidence.Single(static x => x.Label == "Blocker 1 (oldest transaction)").Value.Should().StartWith("pid " + holderPid);
            wait.Finding.Caveat.Should().Contain("pg_locks.waitstart", "PostgreSQL 17 has it");

            HealthReportFinding waitUnnamed = unnamed.Findings.Should().ContainSingle(static x => x.Code == "C06").Subject;
            waitUnnamed.Evidence.Where(static x => x.Label.StartsWith("Waiter", StringComparison.Ordinal) || x.Label.StartsWith("Blocker", StringComparison.Ordinal))
                .Should().NotBeEmpty().And.OnlyContain(static x => x.Value.StartsWith("withheld - ", StringComparison.Ordinal));
            waitUnnamed.Evidence.Single(static x => x.Label == "Waiting for").Value.Should().Contain("mt_streams row");

            // Database › Sessions: counts for both, names for one.
            await using (AsyncServiceScope circuit = open.Circuit())
            {
                ISessionActivityService service = circuit.ServiceProvider.GetRequiredService<ISessionActivityService>();

                SessionActivitySummary summary = await service.GetSummaryAsync(open.Scope, Token);
                summary.Waiting.Should().BeGreaterThanOrEqualTo(1);
                summary.Details.IsOpen.Should().BeTrue();

                SessionActivityDetails details = await service.GetDetailsAsync(open.Scope, Token);
                SessionDetailRow row = details.Sessions.Single(x => x.ProcessId == holderPid);
                row.State.Should().Be("idle in transaction");
                row.Statement.Text.Should().Contain(streams, "the store's own schema is not withheld, and the text is the statement as written");
                details.Locks.Should().Contain(x => x.ProcessId == holderPid && x.Relation == SampleStore.EventSchema + ".mt_streams");
                details.Sessions.Single(x => x.ProcessId == waiterPid).BlockedBy.Should().Contain(holderPid);
            }

            await using (AsyncServiceScope circuit = closed.Circuit())
            {
                ISessionActivityService service = circuit.ServiceProvider.GetRequiredService<ISessionActivityService>();

                SessionActivitySummary summary = await service.GetSummaryAsync(closed.Scope, Token);
                summary.Details.Refusal.Should().Be(DatabaseRefusal.CapabilityOff);
                summary.Sessions.Should().Contain(static x => x.State == "idle in transaction");

                (await service.GetDetailsAsync(closed.Scope, Token)).Refusal.Should().Be(DatabaseRefusal.CapabilityOff);
            }

            open.Logs.WarningsOrWorse.Should().BeEmpty("a finding is what the page says; the log is for failures");
        }
        finally
        {
            await held.RollbackAsync(CancellationToken.None);
            await waiting;
        }
    }

    [PostgresFact]
    public async Task A_withheld_schema_named_in_a_running_statement_is_masked_unless_the_visitor_may_see_it()
    {
        string database = await ConnectionsLiveHost.CreateDatabaseAsync(postgres.ConnectionString, "health_sessions_masking");

        await using ConnectionsLiveHost activityOnly = await ConnectionsLiveHost.CreateAsync(
            database, studio: static options => options.Capabilities.ViewDatabaseActivity = true);
        await using ConnectionsLiveHost browsing = await ConnectionsLiveHost.CreateAsync(database, studio: static options =>
        {
            options.Capabilities.ViewDatabaseActivity = true;
            options.Capabilities.BrowseDatabase = true;
            options.BrowsableSchemas.Add("h3_hidden");
        });

        await using (NpgsqlConnection setup = await activityOnly.OpenAsync())
        await using (NpgsqlCommand create = new("create schema h3_hidden; create table h3_hidden.secrets (id int); insert into h3_hidden.secrets values (1);", setup))
        {
            await create.ExecuteNonQueryAsync(Token);
        }

        await using NpgsqlConnection runner = await activityOnly.OpenAsync();
        int runnerPid = runner.ProcessID;
        await using NpgsqlCommand sleeping = new("select pg_sleep(30) from h3_hidden.secrets", runner);
        Task running = sleeping.ExecuteNonQueryAsync(Token);

        try
        {
            await LiveWait.UntilAsync(
                async () => await LiveWait.ScalarAsync<bool>(
                    database, "select exists (select 1 from pg_stat_activity where pid = @pid and state = 'active')", ("pid", runnerPid)),
                TimeSpan.FromSeconds(20),
                "the statement to be running");

            SessionActivityDetails masked;

            await using (AsyncServiceScope circuit = activityOnly.Circuit())
            {
                ISessionActivityService service = circuit.ServiceProvider.GetRequiredService<ISessionActivityService>();

                masked = await service.GetDetailsAsync(activityOnly.Scope, Token);
                await service.GetDetailsAsync(activityOnly.Scope, Token);

                circuit.ServiceProvider.GetRequiredService<StudioActionLogService>().GetLatest()
                    .Where(static x => x.Action == SessionActivityService.DetailsAction && x.Succeeded)
                    .Should().ContainSingle("the first read is audited by signature, the second is a refresh");
            }

            masked.StatementsWithheld.Should().BeFalse();
            string? statement = masked.Sessions.Single(x => x.ProcessId == runnerPid).Statement.Text;
            statement.Should().Contain("‹withheld›.secrets").And.NotContain("h3_hidden", "without BrowseDatabase the schema is withheld (D27)");
            masked.Locks.Where(x => x.ProcessId == runnerPid && x.Target == "a relation Marten does not own")
                .Should().NotBeEmpty().And.OnlyContain(static x => x.Relation == null, "a relation in a withheld schema is named nowhere");

            await using (AsyncServiceScope circuit = browsing.Circuit())
            {
                SessionActivityDetails shown = await circuit.ServiceProvider.GetRequiredService<ISessionActivityService>()
                    .GetDetailsAsync(browsing.Scope, Token);

                shown.Sessions.Single(x => x.ProcessId == runnerPid).Statement.Text.Should().Contain("h3_hidden.secrets",
                    "a visitor who may browse the schema sees the statement as written");
                shown.Locks.Should().Contain(x => x.ProcessId == runnerPid && x.Relation == "h3_hidden.secrets");
            }
        }
        finally
        {
            await LiveWait.ScalarAsync<bool>(database, "select pg_cancel_backend(@pid)", ("pid", runnerPid));
            await FinishedAsync(running);
        }
    }

    [PostgresFact]
    [HealthFires("C05")]
    public async Task A_statement_still_running_five_minutes_on_is_a_C05_warning_with_who_behind_the_gate()
    {
        string database = await ConnectionsLiveHost.CreateDatabaseAsync(postgres.ConnectionString, "health_c05_fires");
        await using ConnectionsLiveHost host = await ConnectionsLiveHost.CreateAsync(database, studio: Everything);

        await using NpgsqlConnection runner = await host.OpenAsync();
        int runnerPid = runner.ProcessID;
        await using NpgsqlCommand sleeping = new("select pg_sleep(60)", runner);
        Task running = sleeping.ExecuteNonQueryAsync(Token);

        try
        {
            await LiveWait.UntilAsync(
                async () => await LiveWait.ScalarAsync<bool>(
                    database,
                    "select exists (select 1 from pg_stat_activity where pid = @pid and state = 'active' and now() - query_start > interval '2 seconds')",
                    ("pid", runnerPid)),
                TimeSpan.FromSeconds(20),
                "the statement to have run for two seconds");

            LongRunningStatementCheck check = new();
            LongRunningFacts facts = await host.GatherAsync(check);

            facts.Statements.Should().Contain(x => x.ProcessId == runnerPid, "the gatherer reads the real statement");
            check.Evaluate(facts, HealthEvaluation.At(facts.ReadAt)).Findings.Should().BeEmpty("two seconds is not five minutes");

            // The same snapshot, judged five minutes on: the statement, had nothing changed, would be past the threshold.
            HealthFinding finding = check.Evaluate(facts, HealthEvaluation.At(facts.ReadAt.AddMinutes(5))).Findings.Should().ContainSingle().Subject;
            finding.Severity.Should().Be(HealthSeverity.Warning);
            finding.Evidence.Single(static x => x.Label == "Statement 1 (longest)").Should().Match<HealthEvidence>(
                x => x.Gate == HealthGate.Activity && x.Value.StartsWith("pid " + runnerPid, StringComparison.Ordinal));
            finding.Title.Should().NotContain("pg_sleep", "a finding never carries another application's statement");
        }
        finally
        {
            await LiveWait.ScalarAsync<bool>(database, "select pg_cancel_backend(@pid)", ("pid", runnerPid));
            await FinishedAsync(running);
        }
    }

    [PostgresFact]
    [HealthQuiet("C02")]
    [HealthQuiet("C04")]
    [HealthQuiet("C05")]
    [HealthQuiet("C06")]
    [HealthQuiet("C07")]
    [HealthQuiet("C09")]
    public async Task The_healthy_sample_store_is_quiet_for_the_session_and_budget_checks()
    {
        string database = await ConnectionsLiveHost.CreateDatabaseAsync(postgres.ConnectionString, "health_sessions_quiet");
        await using ConnectionsLiveHost host = await ConnectionsLiveHost.CreateAsync(database, studio: Everything);

        HealthReport report = await host.ReportAsync();

        foreach (string code in new[] { "C02", "C04", "C05", "C06", "C07", "C09" })
        {
            report.Findings.Should().NotContain(x => x.Code == code);
            HealthCheckStatus status = ConnectionsLiveHost.Status(report, code);
            status.Outcome.Should().Be(HealthCheckOutcome.Checked, "{0} is quiet because it looked, not because it did not run: {1}", code, status.Problem);
        }

        ConnectionsLiveHost.Status(report, "C02").Note.Should().Contain("one database");
        ConnectionsLiveHost.Status(report, "C07").Note.Should().Contain("max_prepared_transactions is 0", "the shared container keeps Postgres' default");
        ConnectionsLiveHost.Status(report, "C09").Note.Should().Contain("not hosted in this process");
        host.Logs.WarningsOrWorse.Should().BeEmpty("a superuser with nothing to report is an expected state, never a warning");
    }

    /// <summary>A statement the test cancelled: its 57014 is the point, anything else is not.</summary>
    private static async Task FinishedAsync(Task statement)
    {
        try
        {
            await statement;
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.QueryCanceled)
        {
        }
    }
}
