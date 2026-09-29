using System.Globalization;
using System.Text;

using Npgsql;

using NpgsqlTypes;

namespace MartenStudio.SampleDomain.Relational;

/// <summary>
/// Two Quartz.NET schedulers' worth of jobs, triggers and bookkeeping, written the way Quartz's own
/// ADO.NET job store writes them.
/// </summary>
/// <remarks>
/// <para>
/// <b>The encoding is Quartz's, not a guess.</b> Every instant is <c>DateTimeOffset.UtcTicks</c> in a
/// <c>BIGINT</c> (<c>StdAdoDelegate.GetDbDateTimeValue</c>), every duration is whole milliseconds
/// (<c>GetDbTimeSpanValue</c>), the job data map is UTF-8 JSON in a <c>BYTEA</c>, calendar-interval and
/// daily-time-interval triggers live in <c>qrtz_simprop_triggers</c> under the property slots their
/// persistence delegates use, and the lock rows are the two names Quartz has always used. So what the
/// database browser shows for this schema is what it would show for a real scheduler.
/// </para>
/// <para>
/// <b>Relative to now.</b> Next and previous fire times are computed from the tick count
/// <see cref="RelationalDemoSchema.ApplyAsync" /> reads once, so a freshly seeded demo has triggers that
/// are about to fire rather than ones that fired in 2026. They are written once, when
/// <c>qrtz_job_details</c> is empty, and then left alone like everything else here.
/// </para>
/// </remarks>
internal static class QuartzDemoData
{
    /// <summary>The two scheduler names, which are the first column of every Quartz key.</summary>
    /// <remarks>
    /// A property rather than an initialised field: static fields initialise in textual order, and the
    /// two schedulers are declared at the bottom of the file.
    /// </remarks>
    internal static IReadOnlyList<string> SchedulerNames => [Billing.Name, Reporting.Name];

    /// <summary>Writes everything, if <c>qrtz_job_details</c> is empty.</summary>
    /// <returns>How many rows were written.</returns>
    internal static async Task<int> SeedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long nowTicks,
        CancellationToken cancellationToken)
    {
        if (await DemoTable.HasRowsAsync(connection, transaction, "quartz", "qrtz_job_details", cancellationToken)
                .ConfigureAwait(false))
        {
            return 0;
        }

        await using var batch = new NpgsqlBatch(connection, transaction);

        foreach (Scheduler scheduler in (Scheduler[]) [Billing, Reporting])
        {
            AddScheduler(batch, scheduler, nowTicks);
        }

        return await batch.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void AddScheduler(NpgsqlBatch batch, Scheduler scheduler, long now)
    {
        foreach (Job job in scheduler.Jobs)
        {
            Add(batch,
                """
                insert into quartz.qrtz_job_details
                  (sched_name, job_name, job_group, description, job_class_name,
                   is_durable, is_nonconcurrent, is_update_data, requests_recovery, job_data)
                values (@sched, @name, @group, @description, @class, @durable, @nonconcurrent, @update, @recovery, @data)
                """,
                Text("sched", scheduler.Name),
                Text("name", job.Name),
                Text("group", job.Group),
                Text("description", job.Description),
                Text("class", scheduler.Namespace + "." + job.Name + "Job, " + scheduler.Assembly),
                Bool("durable", job.Durable),
                Bool("nonconcurrent", job.NonConcurrent),
                Bool("update", job.UpdateData),
                Bool("recovery", job.RequestsRecovery),
                Bytes("data", Utf8(job.DataJson)));
        }

        foreach (Trigger trigger in scheduler.Triggers)
        {
            AddTrigger(batch, scheduler, trigger, now);
        }

        foreach (Calendar calendar in scheduler.Calendars)
        {
            Add(batch,
                "insert into quartz.qrtz_calendars (sched_name, calendar_name, calendar) values (@sched, @name, @calendar)",
                Text("sched", scheduler.Name),
                Text("name", calendar.Name),
                Bytes("calendar", Utf8(calendar.Json)));
        }

        // One row per node of the cluster, checked in a few seconds ago at Quartz's default 7.5 s interval.
        for (int i = 0; i < scheduler.Nodes.Length; i++)
        {
            Add(batch,
                """
                insert into quartz.qrtz_scheduler_state (sched_name, instance_name, last_checkin_time, checkin_interval)
                values (@sched, @instance, @checkin, @interval)
                """,
                Text("sched", scheduler.Name),
                Text("instance", scheduler.Nodes[i]),
                Long("checkin", now - TimeSpan.FromSeconds(2 + (3 * i)).Ticks),
                Long("interval", 7_500));
        }

        foreach (string lockName in (string[]) ["TRIGGER_ACCESS", "STATE_ACCESS"])
        {
            Add(batch,
                "insert into quartz.qrtz_locks (sched_name, lock_name) values (@sched, @lock)",
                Text("sched", scheduler.Name),
                Text("lock", lockName));
        }

        AddFiredTriggers(batch, scheduler, now);
        AddHistory(batch, scheduler, now);
    }

    private static void AddTrigger(NpgsqlBatch batch, Scheduler scheduler, Trigger trigger, long now)
    {
        Job job = scheduler.Jobs.Single(x => x.Name == trigger.JobName);

        long? next = trigger.NextIn is { } nextIn ? now + nextIn.Ticks : null;
        long? previous = trigger.PreviousAgo is { } ago ? now - ago.Ticks : null;
        long start = now - TimeSpan.FromDays(trigger.StartedDaysAgo).Ticks;
        long? end = trigger.EndedDaysAgo is { } ended ? now - TimeSpan.FromDays(ended).Ticks : null;

        Add(batch,
            """
            insert into quartz.qrtz_triggers
              (sched_name, trigger_name, trigger_group, job_name, job_group, description,
               next_fire_time, prev_fire_time, priority, trigger_state, trigger_type,
               start_time, end_time, calendar_name, misfire_instr, job_data)
            values (@sched, @name, @group, @job, @jobGroup, @description,
                    @next, @previous, @priority, @state, @type,
                    @start, @end, @calendar, @misfire, @data)
            """,
            Text("sched", scheduler.Name),
            Text("name", trigger.Name),
            Text("group", job.Group),
            Text("job", job.Name),
            Text("jobGroup", job.Group),
            Text("description", trigger.Description),
            Long("next", next),
            Long("previous", previous),
            new NpgsqlParameter("priority", NpgsqlDbType.Integer) { Value = trigger.Priority },
            Text("state", trigger.State),
            Text("type", trigger.Schedule.TypeDiscriminator),
            Long("start", start),
            Long("end", end),
            Text("calendar", trigger.Calendar),
            new NpgsqlParameter("misfire", NpgsqlDbType.Smallint) { Value = trigger.MisfireInstruction },
            Bytes("data", Utf8(trigger.DataJson)));

        NpgsqlParameter[] key = [Text("sched", scheduler.Name), Text("name", trigger.Name), Text("group", job.Group)];

        switch (trigger.Schedule)
        {
            case Cron cron:
                Add(batch,
                    """
                    insert into quartz.qrtz_cron_triggers (sched_name, trigger_name, trigger_group, cron_expression, time_zone_id)
                    values (@sched, @name, @group, @expression, @zone)
                    """,
                    [.. key, Text("expression", cron.Expression), Text("zone", cron.TimeZone)]);
                break;

            case Simple simple:
                Add(batch,
                    """
                    insert into quartz.qrtz_simple_triggers
                      (sched_name, trigger_name, trigger_group, repeat_count, repeat_interval, times_triggered)
                    values (@sched, @name, @group, @count, @interval, @triggered)
                    """,
                    [.. key,
                        Long("count", simple.RepeatCount),
                        Long("interval", (long) simple.Interval.TotalMilliseconds),
                        Long("triggered", simple.TimesTriggered)]);
                break;

            case CalendarInterval calendar:
                // CalendarIntervalTriggerPersistenceDelegate: Int1 interval, String1 unit, Int2 times
                // triggered, Boolean1 preserve hour across DST, Boolean2 skip a day whose hour is missing.
                Add(batch,
                    """
                    insert into quartz.qrtz_simprop_triggers
                      (sched_name, trigger_name, trigger_group, str_prop_1, int_prop_1, int_prop_2,
                       bool_prop_1, bool_prop_2, time_zone_id)
                    values (@sched, @name, @group, @unit, @interval, @triggered, @preserve, false, @zone)
                    """,
                    [.. key,
                        Text("unit", calendar.Unit),
                        new NpgsqlParameter("interval", NpgsqlDbType.Integer) { Value = calendar.Interval },
                        new NpgsqlParameter("triggered", NpgsqlDbType.Integer) { Value = calendar.TimesTriggered },
                        Bool("preserve", calendar.PreserveHourOfDay),
                        Text("zone", calendar.TimeZone)]);
                break;

            case DailyInterval daily:
                // DailyTimeIntervalTriggerPersistenceDelegate: Int1 interval, String1 unit, Int2 times
                // triggered, String2 the days of the week, String3 "h,m,s,h,m,s" start and end of day,
                // Long1 the repeat count.
                Add(batch,
                    """
                    insert into quartz.qrtz_simprop_triggers
                      (sched_name, trigger_name, trigger_group, str_prop_1, str_prop_2, str_prop_3,
                       int_prop_1, int_prop_2, long_prop_1, time_zone_id)
                    values (@sched, @name, @group, @unit, @days, @hours, @interval, @triggered, -1, @zone)
                    """,
                    [.. key,
                        Text("unit", daily.Unit),
                        Text("days", daily.DaysOfWeek),
                        Text("hours", daily.TimeOfDay),
                        new NpgsqlParameter("interval", NpgsqlDbType.Integer) { Value = daily.Interval },
                        new NpgsqlParameter("triggered", NpgsqlDbType.Integer) { Value = daily.TimesTriggered },
                        Text("zone", daily.TimeZone)]);
                break;

            default:
                throw new InvalidOperationException("No Quartz table for " + trigger.Schedule.GetType().Name + ".");
        }
    }

    /// <summary>
    /// What a cluster looks like mid-flight: an acquired trigger about to fire, and an executing one whose
    /// non-concurrent job is holding its other trigger <c>BLOCKED</c>.
    /// </summary>
    private static void AddFiredTriggers(NpgsqlBatch batch, Scheduler scheduler, long now)
    {
        int sequence = 0;

        foreach (Trigger trigger in scheduler.Triggers.Where(x => x.FiredState is not null))
        {
            Job job = scheduler.Jobs.Single(x => x.Name == trigger.JobName);
            string instance = scheduler.Nodes[sequence % scheduler.Nodes.Length];
            sequence++;

            Add(batch,
                """
                insert into quartz.qrtz_fired_triggers
                  (sched_name, entry_id, trigger_name, trigger_group, instance_name, fired_time, sched_time,
                   priority, state, job_name, job_group, is_nonconcurrent, requests_recovery)
                values (@sched, @entry, @name, @group, @instance, @fired, @scheduled,
                        @priority, @state, @job, @jobGroup, @nonconcurrent, @recovery)
                """,
                Text("sched", scheduler.Name),
                Text("entry", instance + (now / TimeSpan.TicksPerMillisecond + sequence).ToString(CultureInfo.InvariantCulture)),
                Text("name", trigger.Name),
                Text("group", job.Group),
                Text("instance", instance),
                Long("fired", now - TimeSpan.FromSeconds(4 * sequence).Ticks),
                Long("scheduled", now - TimeSpan.FromSeconds((4 * sequence) + 1).Ticks),
                new NpgsqlParameter("priority", NpgsqlDbType.Integer) { Value = trigger.Priority },
                Text("state", trigger.FiredState),
                Text("job", job.Name),
                Text("jobGroup", job.Group),
                Bool("nonconcurrent", job.NonConcurrent),
                Bool("recovery", job.RequestsRecovery));
        }
    }

    /// <summary>
    /// Quartz 4's optional execution and misfire history: a run every half hour or so going back a day,
    /// a few of them failed and retried, and two misfires.
    /// </summary>
    private static void AddHistory(NpgsqlBatch batch, Scheduler scheduler, long now)
    {
        Trigger[] recurring = [.. scheduler.Triggers.Where(x => x.PreviousAgo is not null)];

        for (int i = 0; i < 18; i++)
        {
            Trigger trigger = recurring[i % recurring.Length];
            Job job = scheduler.Jobs.Single(x => x.Name == trigger.JobName);
            bool failed = i % 7 == 3;
            string instance = scheduler.Nodes[i % scheduler.Nodes.Length];

            Add(batch,
                """
                insert into quartz.qrtz_execution_history
                  (sched_name, entry_id, instance_name, job_name, job_group, trigger_name, trigger_group,
                   fired_time, run_time, succeeded, error_message, retry_attempt, retry_scheduled, execution_log)
                values (@sched, @entry, @instance, @job, @group, @trigger, @group,
                        @fired, @run, @succeeded, @error, @attempt, @retry, @log)
                """,
                Text("sched", scheduler.Name),
                Text("entry", instance + "-h" + (now / TimeSpan.TicksPerSecond - (i * 1_800)).ToString(CultureInfo.InvariantCulture)),
                Text("instance", instance),
                Text("job", job.Name),
                Text("group", job.Group),
                Text("trigger", trigger.Name),
                Long("fired", now - TimeSpan.FromMinutes(32 + (i * 83)).Ticks),
                Long("run", 180 + (i * 977 % 9_000)),
                Bool("succeeded", !failed),
                Text("error", failed ? "Npgsql.NpgsqlException: Exception while reading from stream" : null),
                new NpgsqlParameter("attempt", NpgsqlDbType.Integer) { Value = failed ? 1 : 0 },
                Bool("retry", failed),
                Text("log", failed ? null : "Processed " + (40 + (i * 13)).ToString(CultureInfo.InvariantCulture) + " item(s)."));
        }

        for (int i = 0; i < 2; i++)
        {
            Trigger trigger = recurring[(i * 3) + 1];
            Job job = scheduler.Jobs.Single(x => x.Name == trigger.JobName);

            Add(batch,
                """
                insert into quartz.qrtz_misfire_history
                  (sched_name, entry_id, instance_name, trigger_name, trigger_group, job_name, job_group,
                   misfire_time, sched_time, reason)
                values (@sched, @entry, @instance, @trigger, @group, @job, @group, @misfire, @scheduled, @reason)
                """,
                Text("sched", scheduler.Name),
                Text("entry", scheduler.Nodes[0] + "-m" + i.ToString(CultureInfo.InvariantCulture)),
                Text("instance", scheduler.Nodes[0]),
                Text("trigger", trigger.Name),
                Text("group", job.Group),
                Text("job", job.Name),
                Long("misfire", now - TimeSpan.FromHours(20 - i).Ticks),
                Long("scheduled", now - TimeSpan.FromHours(20 - i).Ticks - TimeSpan.FromMinutes(2).Ticks),
                new NpgsqlParameter("reason", NpgsqlDbType.Integer) { Value = i + 1 });
        }
    }

    private static void Add(NpgsqlBatch batch, string sql, params NpgsqlParameter[] parameters)
    {
        var command = new NpgsqlBatchCommand(sql);
        command.Parameters.AddRange(parameters);
        batch.BatchCommands.Add(command);
    }

    private static NpgsqlParameter Text(string name, string? value) =>
        new(name, NpgsqlDbType.Text) { Value = (object?) value ?? DBNull.Value };

    private static NpgsqlParameter Long(string name, long? value) =>
        new(name, NpgsqlDbType.Bigint) { Value = value is { } v ? v : DBNull.Value };

    private static NpgsqlParameter Bool(string name, bool value) =>
        new(name, NpgsqlDbType.Boolean) { Value = value };

    private static NpgsqlParameter Bytes(string name, byte[]? value) =>
        new(name, NpgsqlDbType.Bytea) { Value = (object?) value ?? DBNull.Value };

    private static byte[]? Utf8(string? json) => json is null ? null : Encoding.UTF8.GetBytes(json);

    // ---------------------------------------------------------------------------------------------
    // The two schedulers
    // ---------------------------------------------------------------------------------------------

    private static readonly Scheduler Billing = new(
        "BillingScheduler",
        "Acme.Billing.Jobs",
        "Acme.Billing",
        ["billing-node-a-7f3c", "billing-node-b-19d2"],
        [
            new("SendInvoiceReminders", "reminders", "E-mails a reminder for every invoice seven days overdue.", false, true, false, true,
                """{"daysOverdue":7,"template":"reminder-v3","batchSize":250}"""),
            new("GenerateMonthlyInvoices", "invoicing", "Bills every active subscription on the first of the month.", false, true, true, true,
                """{"lastRunPeriod":"2026-08","dryRun":false}"""),
            new("RetryFailedPayments", "invoicing", "Retries card payments the gateway declined as transient.", false, true, false, true,
                """{"maxAttempts":4,"gateway":"stripe"}"""),
            new("SyncExchangeRates", "integration", "Pulls ECB reference rates.", false, false, false, false,
                """{"source":"https://www.ecb.europa.eu/stats/eurofxref/eurofxref-daily.xml"}"""),
            new("PurgeExpiredSessions", "maintenance", "Deletes customer portal sessions idle for 30 days.", false, false, false, false, null),
            new("ReconcileBankStatements", "integration", "Matches incoming bank statement lines to open invoices.", false, true, false, true,
                """{"bank":"OP","account":"FI49 5000 9420 0287 30","toleranceCents":2}"""),
            new("ExportLedgerToErp", "integration", "Posts the day's ledger entries to the ERP.", false, true, false, true,
                """{"endpoint":"erp-gateway","company":"1000"}"""),
            new("CloseAccountingPeriod", "invoicing", "Locks the previous month for posting.", false, true, false, false, null),
            new("SendDunningLetters", "reminders", "Sends the second and final payment demands.", false, true, false, true,
                """{"stages":[14,28],"channel":"letter"}"""),
            new("RecalculateCreditLimits", "invoicing", "One-off recalculation after the risk model change.", false, false, false, false,
                """{"model":"risk-2026.3"}"""),
            new("ArchiveOldInvoices", "maintenance", "Moves invoices older than seven years to cold storage.", false, true, false, false,
                """{"retentionYears":7}"""),
            new("RebuildSearchIndex", "maintenance", "Durable, and fired by hand from the admin screen.", true, true, false, false, null),
        ],
        [
            new("send-invoice-reminders", "SendInvoiceReminders", "Weekdays at nine, Helsinki time.",
                new Cron("0 0 9 ? * MON-FRI", "Europe/Helsinki"), "WAITING", TimeSpan.FromHours(14), TimeSpan.FromHours(10), 120),
            new("send-invoice-reminders-catch-up", "SendInvoiceReminders", "Every four hours, for whatever the morning run missed.",
                new Simple(-1, TimeSpan.FromHours(4), 311), "WAITING", TimeSpan.FromMinutes(95), TimeSpan.FromMinutes(145), 90)
            { Priority = 3 },
            new("generate-monthly-invoices", "GenerateMonthlyInvoices", "02:00 UTC on the first of the month, skipping holidays.",
                new Cron("0 0 2 1 * ?", "UTC"), "WAITING", TimeSpan.FromDays(3), TimeSpan.FromDays(28), 400)
            { Calendar = "FinnishHolidays", Priority = 8, MisfireInstruction = 1 },
            new("retry-failed-payments", "RetryFailedPayments", "Every fifteen minutes.",
                new Simple(-1, TimeSpan.FromMinutes(15), 20_417), "BLOCKED", TimeSpan.FromMinutes(11), TimeSpan.FromMinutes(4), 220)
            { FiredState = "EXECUTING" },
            new("sync-exchange-rates", "SyncExchangeRates", "On the hour and the half hour.",
                new Cron("0 0/30 * * * ?", "UTC"), "WAITING", TimeSpan.FromMinutes(17), TimeSpan.FromMinutes(13), 365),
            new("purge-expired-sessions", "PurgeExpiredSessions", null,
                new Simple(-1, TimeSpan.FromHours(1), 8_760), "WAITING", TimeSpan.FromMinutes(41), TimeSpan.FromMinutes(19), 365)
            { MisfireInstruction = -1 },
            new("reconcile-bank-statements", "ReconcileBankStatements", "Every two hours in office hours, weekdays.",
                new DailyInterval(2, "Hour", 1_204, "2,3,4,5,6", "8,0,0,18,0,0", "Europe/Helsinki"), "WAITING",
                TimeSpan.FromMinutes(73), TimeSpan.FromMinutes(47), 180),
            new("export-ledger-to-erp", "ExportLedgerToErp", "Nightly at 23:15. Paused while the ERP is migrated.",
                new Cron("0 15 23 * * ?", "Europe/Helsinki"), "PAUSED", TimeSpan.FromHours(9), TimeSpan.FromDays(6), 300)
            { MisfireInstruction = 2 },
            new("close-accounting-period", "CloseAccountingPeriod", "Monthly.",
                new CalendarInterval(1, "Month", 17, "Europe/Helsinki", true), "WAITING", TimeSpan.FromDays(2), TimeSpan.FromDays(29), 540),
            new("send-dunning-letters", "SendDunningLetters", "Weekly.",
                new CalendarInterval(1, "Week", 52, "Europe/Helsinki", false), "WAITING", TimeSpan.FromDays(4), TimeSpan.FromDays(3), 370),
            new("recalculate-credit-limits", "RecalculateCreditLimits", "Once, on Friday night.",
                new Simple(0, TimeSpan.Zero, 0), "WAITING", TimeSpan.FromDays(3) + TimeSpan.FromHours(7), null, 1)
            { DataJson = """{"notify":"risk-team@acme.example"}""" },
            new("archive-old-invoices", "ArchiveOldInvoices", "Sundays at 03:00. Failed on the last run: the archive bucket is read-only.",
                new Cron("0 0 3 ? * SUN", "UTC"), "ERROR", null, TimeSpan.FromDays(2), 700),
        ],
        [new("FinnishHolidays", """{"$type":"HolidayCalendar","description":"Finnish public holidays","timeZoneId":"Europe/Helsinki","excludedDates":["2026-12-06","2026-12-24","2026-12-25","2026-12-26","2027-01-01","2027-01-06"]}""")]);

    private static readonly Scheduler Reporting = new(
        "ReportingScheduler",
        "Acme.Reporting.Jobs",
        "Acme.Reporting",
        ["reporting-01"],
        [
            new("DailySalesReport", "reports", "Yesterday's sales by region, as PDF and CSV.", false, true, false, false,
                """{"formats":["pdf","csv"],"recipients":["sales-leads@acme.example"]}"""),
            new("WeeklyKpiDigest", "reports", "Monday morning KPI digest for the management team.", false, false, false, false,
                """{"recipients":["management@acme.example"]}"""),
            new("RefreshDashboards", "reports", "Recomputes the operations dashboard tiles.", false, true, false, false, null),
            new("ExportCustomersCsv", "exports", "Full customer export for the CRM.", false, true, false, true,
                """{"target":"sftp://crm.acme.example/incoming","gzip":true}"""),
            new("PushWarehouseSnapshot", "exports", "Sends stock levels to the marketplace integration.", false, true, true, true,
                """{"lastSnapshot":"2026-09-29T05:40:00Z"}"""),
            new("MonthlyFinanceReport", "reports", "Month-end finance pack.", false, true, false, false, null),
            new("QuarterlyBoardPack", "reports", "Board pack, first working day of the quarter.", false, true, false, false,
                """{"confidential":true}"""),
            new("BusinessHoursHeartbeat", "exports", "Tells the partner portal the feed is alive.", false, false, false, false, null),
            new("CleanupTempFiles", "maintenance", "Removes rendered reports older than a day.", false, false, false, false,
                """{"olderThanHours":24}"""),
            new("VacuumReportCache", "maintenance", "Trims the report cache table.", false, true, false, false, null),
            new("YearEndArchive", "maintenance", "Archived the 2025 reports. Done.", false, true, false, false, null),
            new("RegenerateReport", "reports", "Durable, and fired on demand with the report id in the trigger's data.", true, false, false, false, null),
        ],
        [
            new("daily-sales-report", "DailySalesReport", "06:30 every day.",
                new Cron("0 30 6 * * ?", "Europe/Helsinki"), "WAITING", TimeSpan.FromHours(21), TimeSpan.FromHours(3), 300)
            { DataJson = """{"reportFormat":"pdf"}""" },
            new("daily-sales-report-refresh", "DailySalesReport", "Every thirty minutes, CSV only.",
                new Simple(-1, TimeSpan.FromMinutes(30), 14_025), "WAITING", TimeSpan.FromMinutes(8), TimeSpan.FromMinutes(22), 300)
            { DataJson = """{"reportFormat":"csv"}""", Priority = 2 },
            new("weekly-kpi-digest", "WeeklyKpiDigest", "Mondays at 07:00.",
                new Cron("0 0 7 ? * MON", "Europe/Helsinki"), "WAITING", TimeSpan.FromDays(6), TimeSpan.FromDays(1), 420),
            new("refresh-dashboards", "RefreshDashboards", "Every five minutes.",
                new Simple(-1, TimeSpan.FromMinutes(5), 120_960), "ACQUIRED", TimeSpan.FromSeconds(20), TimeSpan.FromMinutes(5) - TimeSpan.FromSeconds(20), 420)
            { FiredState = "ACQUIRED" },
            new("export-customers-csv", "ExportCustomersCsv", "01:00 UTC nightly.",
                new Cron("0 0 1 * * ?", "UTC"), "WAITING", TimeSpan.FromHours(18), TimeSpan.FromHours(6), 250),
            new("push-warehouse-snapshot", "PushWarehouseSnapshot", "Every ten minutes.",
                new Simple(-1, TimeSpan.FromMinutes(10), 36_288), "BLOCKED", TimeSpan.FromMinutes(6), TimeSpan.FromMinutes(4), 250)
            { FiredState = "EXECUTING" },
            new("monthly-finance-report", "MonthlyFinanceReport", "Monthly.",
                new CalendarInterval(1, "Month", 9, "Europe/Helsinki", true), "WAITING", TimeSpan.FromDays(2), TimeSpan.FromDays(29), 280),
            new("quarterly-board-pack", "QuarterlyBoardPack", "Every three months.",
                new CalendarInterval(3, "Month", 3, "Europe/Helsinki", true), "WAITING", TimeSpan.FromDays(2), TimeSpan.FromDays(91), 280),
            new("business-hours-heartbeat", "BusinessHoursHeartbeat", "Every fifteen minutes, nine to five, weekdays.",
                new DailyInterval(15, "Minute", 6_210, "2,3,4,5,6", "9,0,0,17,0,0", "Europe/Helsinki"), "WAITING",
                TimeSpan.FromMinutes(12), TimeSpan.FromMinutes(3), 200),
            new("cleanup-temp-files", "CleanupTempFiles", "04:00 nightly.",
                new Cron("0 0 4 * * ?", "UTC"), "WAITING", TimeSpan.FromHours(15), TimeSpan.FromHours(9), 200),
            new("vacuum-report-cache", "VacuumReportCache", "Every six hours.",
                new Simple(-1, TimeSpan.FromHours(6), 1_120), "WAITING", TimeSpan.FromHours(2), TimeSpan.FromHours(4), 280),
            new("year-end-archive", "YearEndArchive", "05:00 on New Year's Day, 2026 only.",
                new Cron("0 0 5 1 1 ?", "UTC"), "COMPLETE", null, TimeSpan.FromDays(271), 400)
            { EndedDaysAgo = 200 },
        ],
        [new("CompanyShutdown", """{"$type":"AnnualCalendar","description":"Summer shutdown","excludedDays":["07-13","07-14","07-15","07-16","07-17"]}""")]);

    // ---------------------------------------------------------------------------------------------
    // The shapes
    // ---------------------------------------------------------------------------------------------

    private sealed record Scheduler(
        string Name,
        string Namespace,
        string Assembly,
        string[] Nodes,
        Job[] Jobs,
        Trigger[] Triggers,
        Calendar[] Calendars);

    private sealed record Job(
        string Name,
        string Group,
        string Description,
        bool Durable,
        bool NonConcurrent,
        bool UpdateData,
        bool RequestsRecovery,
        string? DataJson);

    private sealed record Trigger(
        string Name,
        string JobName,
        string? Description,
        Schedule Schedule,
        string State,
        TimeSpan? NextIn,
        TimeSpan? PreviousAgo,
        int StartedDaysAgo)
    {
        public int Priority { get; init; } = 5;

        public short MisfireInstruction { get; init; }

        public string? Calendar { get; init; }

        public string? DataJson { get; init; }

        public int? EndedDaysAgo { get; init; }

        /// <summary>The <c>qrtz_fired_triggers</c> state, for a trigger that is mid-firing.</summary>
        public string? FiredState { get; init; }
    }

    private sealed record Calendar(string Name, string Json);

    private abstract record Schedule(string TypeDiscriminator);

    private sealed record Cron(string Expression, string TimeZone) : Schedule("CRON");

    private sealed record Simple(long RepeatCount, TimeSpan Interval, long TimesTriggered) : Schedule("SIMPLE");

    private sealed record CalendarInterval(int Interval, string Unit, int TimesTriggered, string TimeZone, bool PreserveHourOfDay)
        : Schedule("CAL_INT");

    private sealed record DailyInterval(
        int Interval,
        string Unit,
        int TimesTriggered,
        string DaysOfWeek,
        string TimeOfDay,
        string TimeZone) : Schedule("DAILY_I");
}
