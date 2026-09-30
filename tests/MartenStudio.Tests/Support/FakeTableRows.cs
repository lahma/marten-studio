using System.Globalization;
using System.Text;

using MartenStudio.Internal.Sql;
using MartenStudio.Services.Database;
using MartenStudio.Services.Documents;
using MartenStudio.Services.Query;

namespace MartenStudio.Tests.Support;

/// <summary>
/// Quartz-shaped rows for the Rows tab's page tests: a three-column key, NULLs, a JSON cell, a
/// <c>bytea</c> cell holding JSON, and a tick <c>BIGINT</c> - built from the DTOs the row service really
/// returns, so a page test draws what a live page draws.
/// </summary>
/// <remarks>
/// Every row is derived from its index, so a page, a row detail and a cell asked for the same row agree
/// with each other without a table behind them.
/// </remarks>
internal static class FakeTableRows
{
    /// <summary>The schema.</summary>
    public const string Schema = "quartz";

    /// <summary>The table.</summary>
    public const string Table = "qrtz_triggers";

    /// <summary>The scheduler every row belongs to.</summary>
    public const string Scheduler = "QuartzScheduler";

    /// <summary>The foreign key to the job a trigger fires.</summary>
    public const string JobForeignKey = "fk_qrtz_triggers_job_details";

    /// <summary>Tick 0 of the demo: 2026-09-29T00:00:00Z in .NET ticks, the unit Quartz stores.</summary>
    public const long BaseTicks = 639_262_368_000_000_000;

    /// <summary>The row key.</summary>
    public static DatabaseRowKey Key { get; } =
        new(DatabaseRowKeySource.PrimaryKey, "qrtz_triggers_pkey", ["sched_name", "trigger_name", "trigger_group"]);

    /// <summary>Every column, as the header draws it.</summary>
    public static IReadOnlyList<TableRowColumn> Columns { get; } =
    [
        Column("sched_name", 1, "text", nullable: false, key: 1, index: IndexVerdictLevel.Green),
        Column("trigger_name", 2, "text", nullable: false, key: 2, index: IndexVerdictLevel.Amber),
        Column("trigger_group", 3, "text", nullable: false, key: 3, index: IndexVerdictLevel.Amber),
        Column("job_name", 4, "text", nullable: false, references: [new TableRowColumnReference(JobForeignKey, Schema, "qrtz_job_details")]),
        Column("next_fire_time", 5, "bigint", index: IndexVerdictLevel.Green),
        Column("calendar_name", 6, "text"),
        Column("job_data", 7, "bytea"),
        Column("trigger_meta", 8, "jsonb"),
    ];

    /// <summary>The key of row <paramref name="index" />.</summary>
    public static IReadOnlyDictionary<string, string> KeyOf(int index) => new Dictionary<string, string>
    {
        ["sched_name"] = Scheduler,
        ["trigger_name"] = TriggerName(index),
        ["trigger_group"] = Group(index),
    };

    /// <summary>A page of <paramref name="count" /> rows from <paramref name="first" />, sorted by the key.</summary>
    public static TableRowPage Page(int count = 5, int first = 0, bool hasMore = true, string? filter = null)
    {
        List<TableRow> rows = [.. Enumerable.Range(first, count).Select(Row)];

        return new TableRowPage
        {
            State = TableRowPageState.Loaded,
            Schema = Schema,
            Name = Table,
            Kind = DatabaseObjectKind.Table,
            Ownership = new DatabaseObjectOwnership(DatabaseObjectOwner.Other, RecognisedAs: "Quartz.NET"),
            RowKey = Key,
            EstimatedRows = 24,
            Columns = Columns,
            AvailableColumns = Columns,
            Rows = rows,
            HasMore = hasMore,
            NextCursor = hasMore && rows.Count > 0
                ? new TableRowCursor(null, false, null, [Scheduler, TriggerName(first + count - 1), Group(first + count - 1)])
                : null,
            Paging = new TableRowPaging(TableRowPagingMode.Key, Key.Columns, null, SortDirection.Ascending, 0, null),
            Verdict = new RowFilterVerdict
            {
                Chips = [new SearchChip(SearchChipKind.Sort, "sort", IndexVerdictLevel.Green, "The row key's index serves this order.", null)],
            },
            Sql = "select t.\"sched_name\"::text,\n       t.\"trigger_name\"::text,\n       t.\"trigger_group\"::text\nfrom \"quartz\".\"qrtz_triggers\" as t\n" +
                  (filter is null ? string.Empty : "where t.\"trigger_group\" = @f1\n") +
                  "order by t.\"sched_name\", t.\"trigger_name\", t.\"trigger_group\"\nlimit @limit",
            ParameterNames = filter is null ? ["@cap", "@jsonCap", "@bytes", "@limit"] : ["@cap", "@jsonCap", "@bytes", "@f1", "@limit"],
        };
    }

    /// <summary>A page the service withheld until "Run anyway".</summary>
    public static TableRowPage Withheld() => Page(0, hasMore: false) with
    {
        State = TableRowPageState.Withheld,
        EstimatedRows = 2_400_000,
        Reason = "Not read yet: quartz.qrtz_triggers holds about 2,400,000 rows (more than MartenStudioOptions.ExactCountThreshold, " +
                 "100,000), and a filter no index serves would read every one of them. Run anyway to read it, or filter on an indexed column.",
        Verdict = new RowFilterVerdict
        {
            FilterLevel = IndexVerdictLevel.Red,
            Chips =
            [
                new SearchChip(SearchChipKind.Predicate, "calendar_name ~ business", IndexVerdictLevel.Red,
                    "A substring match reads the text of every row; no index serves it.", null),
                new SearchChip(SearchChipKind.Sort, "sort", IndexVerdictLevel.Green, "The row key's index serves this order.", null),
            ],
        },
    };

    /// <summary>One row, every column.</summary>
    public static TableRowDetail Detail(int index = 0) => new()
    {
        Found = true,
        Schema = Schema,
        Name = Table,
        Ownership = new DatabaseObjectOwnership(DatabaseObjectOwner.Other, RecognisedAs: "Quartz.NET"),
        RowKey = Key,
        Key = KeyOf(index),
        Columns = Columns,
        Cells = Row(index).Cells,
        Sql = "select … from \"quartz\".\"qrtz_triggers\" as t\nwhere t.\"sched_name\" = @k1\n  and t.\"trigger_name\" = @k2\n  and t.\"trigger_group\" = @k3\nlimit 1",
        ParameterNames = ["@cap", "@jsonCap", "@bytes", "@k1", "@k2", "@k3"],
    };

    /// <summary>
    /// A row's references: the job it fires (present, keyed), and two tables pointing at it - one counted,
    /// one past the cap.
    /// </summary>
    public static TableRowReferences References(int index = 0) => new()
    {
        Found = true,
        Outbound =
        [
            new RowOutboundReference(
                JobForeignKey,
                ["sched_name", "job_name", "job_group"],
                [Scheduler, JobName(index), Group(index)],
                Schema,
                "qrtz_job_details",
                ["sched_name", "job_name", "job_group"],
                Validated: true,
                RowReferenceState.Present,
                TargetsRowKey: true,
                new Dictionary<string, string> { ["sched_name"] = Scheduler, ["job_name"] = JobName(index), ["job_group"] = Group(index) },
                "sched_name = " + Scheduler + " job_name = " + JobName(index) + " job_group = " + Group(index),
                null,
                null,
                null,
                null),
        ],
        Inbound =
        [
            new RowInboundReference(
                "qrtz_simple_triggers_sched_name_trigger_name_trigger_group_fkey",
                Schema,
                "qrtz_simple_triggers",
                ["sched_name", "trigger_name", "trigger_group"],
                ["sched_name", "trigger_name", "trigger_group"],
                [Scheduler, TriggerName(index), Group(index)],
                RowInboundState.Counted,
                1,
                false,
                "sched_name = " + Scheduler + " trigger_name = " + TriggerName(index) + " trigger_group = " + Group(index),
                null,
                "cascade",
                null),
            new RowInboundReference(
                "fk_qrtz_fired_triggers",
                Schema,
                "qrtz_fired_triggers",
                ["sched_name", "trigger_name", "trigger_group"],
                ["sched_name", "trigger_name", "trigger_group"],
                [Scheduler, TriggerName(index), Group(index)],
                RowInboundState.Counted,
                1000,
                true,
                "sched_name = " + Scheduler + " trigger_name = " + TriggerName(index) + " trigger_group = " + Group(index),
                null,
                "no action",
                null),
        ],
        Statements = ["select t.\"sched_name\"::text … limit 1"],
    };

    /// <summary>An exact count.</summary>
    public static TableRowCount Count(long rows = 24) => new()
    {
        Count = DocumentCount.Exact(rows),
        Sql = "select pg_catalog.count(*)\nfrom \"quartz\".\"qrtz_triggers\" as t",
    };

    /// <summary>One cell of row <paramref name="index" />, whole.</summary>
    public static TableCellValue Cell(string column, int index = 0)
    {
        TableRowColumn described = Columns.FirstOrDefault(x => string.Equals(x.Name, column, StringComparison.Ordinal))
            ?? throw new ArgumentException("There is no column " + column + ".", nameof(column));

        return column switch
        {
            "job_data" => new TableCellValue
            {
                Found = true,
                Column = described,
                Bytes = JobData(index),
                FullLength = JobData(index).Length,
                Cap = 512 * 1024,
            },
            "calendar_name" when Calendar(index) is null => new TableCellValue
            {
                Found = true,
                Column = described,
                IsNull = true,
                Cap = 512 * 1024,
            },
            _ => new TableCellValue
            {
                Found = true,
                Column = described,
                Text = Text(column, index),
                FullLength = Encoding.UTF8.GetByteCount(Text(column, index) ?? string.Empty),
                Cap = 512 * 1024,
            },
        };
    }

    /// <summary>Row <paramref name="index" />, as a page reads it.</summary>
    public static TableRow Row(int index)
    {
        byte[] data = JobData(index);
        string meta = Meta(index);

        return new TableRow(
            KeyOf(index),
            null,
            [
                new SqlCell(Scheduler, SqlCellKind.Text, false, Scheduler.Length),
                new SqlCell(TriggerName(index), SqlCellKind.Text, false, TriggerName(index).Length),
                new SqlCell(Group(index), SqlCellKind.Text, false, Group(index).Length),
                new SqlCell(JobName(index), SqlCellKind.Text, false, JobName(index).Length),
                NextFireTime(index) is { } ticks
                    ? new SqlCell(ticks.ToString(CultureInfo.InvariantCulture), SqlCellKind.Number, false, 18)
                    : SqlCell.Null,
                Calendar(index) is { } calendar ? new SqlCell(calendar, SqlCellKind.Text, false, calendar.Length) : SqlCell.Null,
                new SqlCell("\\x" + Convert.ToHexStringLower(data) + " (" + data.Length.ToString(CultureInfo.InvariantCulture) + " bytes)",
                    SqlCellKind.Binary, false, data.Length),
                new SqlCell(meta, SqlCellKind.Json, false, meta.Length),
            ]);
    }

    private static string TriggerName(int index) => "trigger-" + index.ToString("00", CultureInfo.InvariantCulture);

    private static string Group(int index) => index % 2 == 0 ? "DEFAULT" : "reports";

    private static string JobName(int index) => "job-" + (index / 2).ToString(CultureInfo.InvariantCulture);

    /// <summary>NULL for every third trigger - paused, never scheduled again.</summary>
    private static long? NextFireTime(int index) => index % 3 == 0 ? null : BaseTicks + (index * TimeSpan.TicksPerHour);

    private static string? Calendar(int index) => index % 4 == 0 ? "business-days" : null;

    /// <summary>Quartz's job data map: UTF-8 JSON in a bytea.</summary>
    private static byte[] JobData(int index) =>
        Encoding.UTF8.GetBytes("{\"reportId\":" + index.ToString(CultureInfo.InvariantCulture) + ",\"format\":\"pdf\"}");

    private static string Meta(int index) =>
        "{\"retries\":" + (index % 3).ToString(CultureInfo.InvariantCulture) + ",\"owner\":\"ops\"}";

    private static string? Text(string column, int index) => column switch
    {
        "sched_name" => Scheduler,
        "trigger_name" => TriggerName(index),
        "trigger_group" => Group(index),
        "job_name" => JobName(index),
        "next_fire_time" => NextFireTime(index)?.ToString(CultureInfo.InvariantCulture),
        "calendar_name" => Calendar(index),
        "trigger_meta" => Meta(index),
        _ => null,
    };

    private static TableRowColumn Column(
        string name,
        int position,
        string type,
        bool nullable = true,
        int? key = null,
        IReadOnlyList<TableRowColumnReference>? references = null,
        IndexVerdictLevel index = IndexVerdictLevel.Red) =>
        new(
            name,
            position,
            type,
            nullable,
            Sortable: type != "json",
            Shown: true,
            TableRowQueryBuilder.KindOf(type),
            key,
            references ?? [],
            index,
            index switch
            {
                IndexVerdictLevel.Green => name + " leads an index.",
                IndexVerdictLevel.Amber => name + " is in an index, but does not lead it.",
                _ => "No index covers " + name + "; filtering or sorting on it reads every row.",
            },
            null);
}
