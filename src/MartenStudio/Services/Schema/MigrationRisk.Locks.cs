using System.Text.RegularExpressions;

namespace MartenStudio.Services.Schema;

/// <summary>The table-level lock modes a migration's statements take, weakest first, spelled as <c>pg_locks</c> spells them.</summary>
internal enum TableLockMode
{
    /// <summary>Taken by a read of the table; conflicts only with <see cref="AccessExclusive" />.</summary>
    AccessShare,

    /// <summary><c>DROP INDEX CONCURRENTLY</c> and <c>CREATE INDEX CONCURRENTLY</c>: blocks neither reads nor writes.</summary>
    ShareUpdateExclusive,

    /// <summary>A plain <c>CREATE INDEX</c>: reads go on, every insert, update and delete waits.</summary>
    Share,

    /// <summary>Adding a foreign key, on both tables: reads go on, every insert, update and delete waits while it validates.</summary>
    ShareRowExclusive,

    /// <summary>Conflicts with every other lock, reads included.</summary>
    AccessExclusive,
}

/// <summary>Whether a statement writes the whole table again.</summary>
internal enum TableRewrite
{
    /// <summary>It does not.</summary>
    No,

    /// <summary>
    /// The studio does not judge it: the statement is an <c>ALTER TABLE</c> form it has no rule for, and most of them take
    /// <c>ACCESS EXCLUSIVE</c>.
    /// </summary>
    Unknown,

    /// <summary>It does unless the new type is binary-coercible with the old (a longer <c>varchar</c>, say).</summary>
    Maybe,

    /// <summary>It does.</summary>
    Yes,
}

/// <summary>One statement in a migration that takes a lock on a table that already exists.</summary>
/// <param name="Kind">What it does, in the words the apply section uses.</param>
/// <param name="Statement">The line from the script, trimmed.</param>
/// <param name="Schema">The schema the statement names its relation in, or <see langword="null" /> when it names none.</param>
/// <param name="Name">The relation the statement names, folded the way Postgres folds an identifier.</param>
/// <param name="IsIndex">Whether <paramref name="Name" /> is an index, whose table the statement does not name.</param>
/// <param name="Lock">The lock it takes on the table (on an index's table, for an index).</param>
/// <param name="Rewrite">Whether it rewrites the table.</param>
/// <param name="Effect">What it does to the table's readers and writers, in a sentence.</param>
/// <param name="IsFunction">
/// Whether <paramref name="Name" /> is a function dropped with <c>CASCADE</c>: no relation of its own, but it removes every index or
/// default built on it, under those tables' locks.
/// </param>
internal sealed record TableLockStatement(
    string Kind,
    string Statement,
    string? Schema,
    string Name,
    bool IsIndex,
    TableLockMode Lock,
    TableRewrite Rewrite,
    string Effect,
    bool IsFunction = false)
{
    /// <summary>The relation as a person reads it: <c>schema.name</c>, or the bare name.</summary>
    public string Display => Schema is null ? Name : Schema + "." + Name;

    /// <summary>The lock as PostgreSQL spells it.</summary>
    public string LockName => Lock switch
    {
        TableLockMode.AccessShare => "ACCESS SHARE",
        TableLockMode.ShareUpdateExclusive => "SHARE UPDATE EXCLUSIVE",
        TableLockMode.Share => "SHARE",
        TableLockMode.ShareRowExclusive => "SHARE ROW EXCLUSIVE",
        _ => "ACCESS EXCLUSIVE",
    };

    /// <summary>Whether this is a plain <c>CREATE INDEX</c> that the migrator could have built concurrently.</summary>
    public bool IsBlockingIndexBuild => Lock == TableLockMode.Share;

    /// <summary>Whether readers wait for it as well as writers.</summary>
    public bool BlocksReaders => Lock == TableLockMode.AccessExclusive;

    /// <summary>The rewrite as a short phrase for a table cell.</summary>
    public string RewriteText => Rewrite switch
    {
        TableRewrite.Yes => "rewrites the table",
        TableRewrite.Maybe => "rewrites the table unless the new type is binary-coercible",
        TableRewrite.Unknown => "the studio does not judge whether it rewrites the table",
        _ => "no rewrite",
    };
}

/// <summary>
/// What each statement of a migration locks, on a table that already exists, and whether it rewrites it - the second half of
/// the apply section's notes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Notes, never a refusal.</b> Like <see cref="Find" /> and <see cref="Notes" />, a text scan of SQL Weasel wrote: it
/// decides nothing and refuses nothing.
/// </para>
/// <para>
/// <b>A complete list for <c>ALTER TABLE</c> and the partition create.</b> Explicit shapes: a plain <c>CREATE INDEX</c>, a dropped
/// index, table, column or constraint, a changed column type, a primary key added, a <c>SET NOT NULL</c>, a foreign key added (its
/// <c>ALTER TABLE</c>, <c>ADD CONSTRAINT</c> and <c>REFERENCES</c> lines are joined), a renamed constraint, a column added with a
/// volatile default or with Marten's backfilling <c>update</c>, <c>CREATE TABLE … PARTITION OF</c> an existing parent, and the
/// partition rebuild's copy out. Any <i>other</i> <c>ALTER TABLE</c> on an existing table is listed too, as one the studio does not
/// judge (<see cref="TableRewrite.Unknown" />, <c>ACCESS EXCLUSIVE</c>): most forms take that lock. A <c>drop function … cascade</c>
/// is listed too: Weasel drops a replaced or removed function that way, and CASCADE removes every index or default built on it (Marten's
/// computed indexes on <c>DateTime</c> members use <c>mt_immutable_timestamp</c> and its siblings) under that table's <c>ACCESS
/// EXCLUSIVE</c> lock. A new table's foreign key to an existing table is listed on the existing table. What is not listed - other
/// functions, sequences, new tables and their indexes - locks no table that already holds data. A relation is "existing" until the
/// script creates it, in order: the rebuild's <c>create table s.t_temp as select * from s.t</c>, <c>drop table s.t cascade</c> and <c>CREATE TABLE
/// s.t</c> are about <c>s.t</c> before the create and about nothing after it, and <c>s.t_temp</c> is the script's own throughout.
/// </para>
/// <para>
/// <b>A line is the unit of classification, and the lock is held for the whole command.</b> Weasel splits a script only on the
/// concurrent-index markers, so everything between them is one command in one implicit transaction: an <c>ACCESS EXCLUSIVE</c> lock is
/// held until the command ends, and a plain <c>CREATE INDEX</c> after it runs under it. Marten writes several statements on one line for a duplicated field: an added field is <c>alter table s.t add
/// column c type;update s.t set c = …;</c> and a changed type is <c>alter table s.t drop column c;alter table s.t add column c
/// type;update s.t set c = …;</c> (Marten <c>DuplicatedFieldColumn</c>). A line is split on <c>;</c> outside quotes and dollar
/// quotes, classified by its leading <c>ALTER TABLE</c>, and an <c>update</c> of the same table in it means the <c>ACCESS
/// EXCLUSIVE</c> lock is held through a backfill of every row. A line that begins with <c>update</c> is never classified on its own,
/// and the lines inside a dollar-quoted function body are not statements at all (<c>StatementLines</c>).
/// </para>
/// <para>
/// <b>The locks are PostgreSQL's, not Weasel's.</b> A plain <c>CREATE INDEX</c> takes <c>SHARE</c>, so writers wait and readers do
/// not; <c>CREATE</c> and <c>DROP INDEX CONCURRENTLY</c> take <c>SHARE UPDATE EXCLUSIVE</c> and block neither; adding a foreign key
/// takes <c>SHARE ROW EXCLUSIVE</c> on both tables; an <c>ALTER TABLE</c>, a plain <c>DROP INDEX</c> or <c>DROP TABLE</c> and
/// <c>CREATE TABLE … PARTITION OF</c> (on its parent) take <c>ACCESS EXCLUSIVE</c>. (Weasel's remark on
/// <c>PostgresqlMigrator.BuildIndexesConcurrentlyOnAlter</c> calls a plain build <c>ACCESS EXCLUSIVE</c>; the documentation of
/// <c>CREATE INDEX</c> says <c>SHARE</c>, and so does the existing <see cref="BlockingIndexBuildKind" />.)
/// </para>
/// </remarks>
internal static partial class MigrationRisk
{
    /// <summary>The kind of a primary key added to a table.</summary>
    internal const string AddsPrimaryKeyKind = "adds a primary key";

    /// <summary>The kind of a plain index build, as a lock statement.</summary>
    internal const string BuildsIndexKind = "builds an index without CONCURRENTLY";

    /// <summary>The kind of the rebuild's copy out of the table.</summary>
    internal const string CopiesTableKind = "copies the table out (partition rebuild)";

    /// <summary>The kind of Marten's added duplicated field: <c>add column</c> then a backfilling <c>update</c>, in one command.</summary>
    internal const string AddsDuplicatedColumnKind = "adds a duplicated column and fills it";

    /// <summary>The kind of a column added with a volatile default, which writes every row.</summary>
    internal const string AddsVolatileColumnKind = "adds a column with a volatile default";

    /// <summary>The kind of <c>alter column … set not null</c>.</summary>
    internal const string SetsNotNullKind = "sets a column not null";

    /// <summary>The kind of a foreign key added to a table.</summary>
    internal const string AddsForeignKeyKind = "adds a foreign key";

    /// <summary>The kind of the other end of a foreign key added.</summary>
    internal const string ReferencedByForeignKeyKind = "is referenced by a new foreign key";

    /// <summary>The kind of <c>create table … partition of</c> an existing parent.</summary>
    internal const string AddsPartitionKind = "adds a partition";

    /// <summary>The kind of <c>alter table … rename constraint</c>.</summary>
    internal const string RenamesConstraintKind = "renames a constraint";

    /// <summary>The kind of any other <c>ALTER TABLE</c>.</summary>
    internal const string AltersTableKind = "alters the table";

    /// <summary>The kind of Weasel's <c>drop function … cascade</c>, which takes every index or default built on the function with it.</summary>
    internal const string DropsFunctionCascadeKind = "drops a function with CASCADE";

    /// <summary>
    /// Why a plain index build is worth moving to <c>CONCURRENTLY</c>, and the setting that does it - Weasel 9.48.0's
    /// <c>PostgresqlMigrator.BuildIndexesConcurrentlyOnAlter</c>, reached through Marten 9.57's
    /// <c>StoreOptions.Advanced.Migrator</c>.
    /// </summary>
    internal const string ConcurrentIndexAdvice =
        "To build these indexes without making writers wait, set opts.Advanced.Migrator.BuildIndexesConcurrentlyOnAlter = true "
        + "in the host's StoreOptions. Marten then writes CREATE INDEX CONCURRENTLY for an index added to a table that already "
        + "exists, and the preview above shows it. A concurrent build cannot run inside a transaction, and one that fails leaves "
        + "an invalid index that has to be dropped before the next attempt.";

    /// <summary>What an <c>ACCESS EXCLUSIVE</c> statement does to the sessions that arrive while it waits and while it runs.</summary>
    internal const string AccessExclusiveAdvice =
        "An ACCESS EXCLUSIVE statement waits for every session using the table to finish, and while it waits it queues every "
        + "reader and writer that arrives behind it - one long transaction on the table can stop the application. Once it has the "
        + "lock it keeps it until the whole command it is in has finished: Weasel sends the statements between index markers as one "
        + "command, so a backfilling UPDATE after it, or an index build, runs under the lock. Apply at a quiet time, or run the "
        + "migration from the host's own migration path with SET lock_timeout (for example '5s'), so it gives up rather than queue. "
        + "This screen's apply sets no lock_timeout.";

    /// <summary>Functions that make a column default volatile, so adding the column writes every row.</summary>
    private static readonly Regex VolatileDefault = new(
        @"\b(random|clock_timestamp|gen_random_uuid|uuid_generate_v1mc|uuid_generate_v1|uuid_generate_v4|uuidv4|uuidv7|nextval|timeofday)\s*\(",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(5));

    /// <summary>The statements in <paramref name="sql" /> that lock a table that already exists, in the order they would run.</summary>
    /// <param name="sql">The migration script, exactly as it would be executed.</param>
    public static IReadOnlyList<TableLockStatement> LockStatements(string? sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            return [];
        }

        List<string> lines = StatementLines(sql);
        HashSet<string> created = new(StringComparer.Ordinal);
        List<TableLockStatement> found = [];

        for (int i = 0; i < lines.Count; i++)
        {
            if (IsPartitionIndexResume(lines, i))
            {
                continue;
            }

            string line = lines[i];

            // Weasel writes an added foreign key over three lines: ALTER TABLE x / ADD CONSTRAINT … FOREIGN KEY(…) / REFERENCES y(…);
            if (ForeignKeyLines(lines, i) is { } span)
            {
                line = string.Join(' ', lines.Skip(i).Take(span));
                i += span - 1;
            }

            if (After(line, "create table ") is { } creating)
            {
                CreateTable(line, creating, created, found);
                continue;
            }

            found.AddRange(Classify(line, created));
        }

        return found;
    }

    /// <summary>The advice that goes with <paramref name="statements" />: nothing for a script that locks nothing worth a sentence.</summary>
    public static IReadOnlyList<string> Advice(IReadOnlyList<TableLockStatement> statements)
    {
        ArgumentNullException.ThrowIfNull(statements);

        List<string> advice = [];

        if (statements.Any(static x => x.IsBlockingIndexBuild))
        {
            advice.Add(ConcurrentIndexAdvice);
        }

        if (statements.Any(static x => x.Lock == TableLockMode.AccessExclusive))
        {
            advice.Add(AccessExclusiveAdvice);
        }

        return advice;
    }

    /// <summary>
    /// A line split into its statements on <c>;</c> outside single quotes, double quotes and dollar quotes, each trimmed, empty ones
    /// dropped. Marten writes several on one line.
    /// </summary>
    internal static IReadOnlyList<string> SplitStatements(string line)
    {
        ArgumentNullException.ThrowIfNull(line);

        List<string> parts = [];
        int start = 0;
        char quote = '\0';
        string? dollar = null;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];

            if (dollar is not null)
            {
                if (string.CompareOrdinal(line, i, dollar, 0, dollar.Length) == 0)
                {
                    i += dollar.Length - 1;
                    dollar = null;
                }
            }
            else if (quote != '\0')
            {
                if (c == quote)
                {
                    if (i + 1 < line.Length && line[i + 1] == quote)
                    {
                        i++;
                    }
                    else
                    {
                        quote = '\0';
                    }
                }
            }
            else if (c is '\'' or '"')
            {
                quote = c;
            }
            else if (c == '$')
            {
                int j = i + 1;
                while (j < line.Length && (char.IsLetterOrDigit(line[j]) || line[j] == '_'))
                {
                    j++;
                }

                if (j < line.Length && line[j] == '$' && (j == i + 1 || !char.IsDigit(line[i + 1])))
                {
                    dollar = line[i..(j + 1)];
                    i = j;
                }
            }
            else if (c == ';')
            {
                AddPart(parts, line[start..i]);
                start = i + 1;
            }
        }

        AddPart(parts, line[start..]);

        return parts;

        static void AddPart(List<string> parts, string text)
        {
            string trimmed = text.Trim();

            if (trimmed.Length > 0)
            {
                parts.Add(trimmed);
            }
        }
    }

    /// <summary>
    /// How many lines the foreign key that starts at <paramref name="index" /> takes, or <see langword="null" /> when none does:
    /// a bare <c>ALTER TABLE x</c> line, an <c>ADD CONSTRAINT</c> line, and on to the line that ends the statement.
    /// </summary>
    private static int? ForeignKeyLines(List<string> lines, int index)
    {
        if (!StartsWithWords(lines[index], "alter table ") || lines[index].Contains(';', StringComparison.Ordinal)
            || index + 1 >= lines.Count || !StartsWithWords(lines[index + 1], "add constraint "))
        {
            return null;
        }

        for (int next = index + 1; next < lines.Count && next <= index + 6; next++)
        {
            if (lines[next].EndsWith(';'))
            {
                return next - index + 1;
            }
        }

        return null;
    }

    private static void CreateTable(string line, string creating, HashSet<string> created, List<TableLockStatement> found)
    {
        if (StartsWithWords(creating, "if not exists "))
        {
            creating = creating["if not exists ".Length..].TrimStart();
        }

        if (FirstToken(creating) is not { } target)
        {
            return;
        }

        if (ContainsWords(line, " as select") && After(line, " from ", anywhere: true) is { } from && FirstToken(from) is { } source
            && !created.Contains(Normalize(source)))
        {
            found.Add(LockOf(
                CopiesTableKind,
                line,
                source,
                isIndex: false,
                TableLockMode.AccessShare,
                TableRewrite.Yes,
                "reads every row of the table to copy it out; the script then drops the table and writes the rows back"));
        }
        else if (After(line, " partition of ", anywhere: true) is { } ofParent && FirstToken(ofParent) is { } parent
            && !created.Contains(Normalize(parent)))
        {
            found.Add(LockOf(
                AddsPartitionKind,
                line,
                parent,
                isIndex: false,
                TableLockMode.AccessExclusive,
                TableRewrite.No,
                "takes the parent's lock to create the partition: every reader and writer of the partitioned table waits for it"));
        }

        created.Add(Normalize(target));
    }

    private static IEnumerable<TableLockStatement> Classify(string line, HashSet<string> created)
    {
        if (StartsWithWords(line, "drop index "))
        {
            string rest = line["drop index ".Length..].TrimStart();
            bool concurrently = StartsWithWords(rest, "concurrently ");

            if (concurrently)
            {
                rest = rest["concurrently ".Length..].TrimStart();
            }

            if (StartsWithWords(rest, "if exists "))
            {
                rest = rest["if exists ".Length..].TrimStart();
            }

            if (FirstToken(rest) is { } index)
            {
                yield return LockOf(
                    DropsIndexKind,
                    line,
                    index,
                    isIndex: true,
                    concurrently ? TableLockMode.ShareUpdateExclusive : TableLockMode.AccessExclusive,
                    TableRewrite.No,
                    concurrently
                        ? "waits for the transactions that could use the index, then drops it; reads and writes go on"
                        : "takes the lock on the index's table and drops the index");
            }

            yield break;
        }

        if (StartsWithWords(line, "drop function ") && ContainsWords(line, " cascade"))
        {
            // Weasel's WriteDropFunction: "drop function if exists s.f(args) cascade;" - CASCADE takes every index or default built on
            // the function with it, under each such table's ACCESS EXCLUSIVE lock. The function names no table.
            string rest = line["drop function ".Length..].TrimStart();

            if (StartsWithWords(rest, "if exists "))
            {
                rest = rest["if exists ".Length..].TrimStart();
            }

            if (FirstToken(rest) is { } function)
            {
                yield return LockOf(
                    DropsFunctionCascadeKind,
                    line,
                    function,
                    isIndex: false,
                    TableLockMode.AccessExclusive,
                    TableRewrite.No,
                    "removes every index or default built on the function, under that table's ACCESS EXCLUSIVE lock",
                    isFunction: true);
            }

            yield break;
        }

        if (StartsWithWords(line, "drop table "))
        {
            string rest = line["drop table ".Length..].TrimStart();

            if (StartsWithWords(rest, "if exists "))
            {
                rest = rest["if exists ".Length..].TrimStart();
            }

            if (FirstToken(rest) is { } table && !created.Contains(Normalize(table)))
            {
                yield return LockOf(
                    DropsTableKind,
                    line,
                    table,
                    isIndex: false,
                    TableLockMode.AccessExclusive,
                    TableRewrite.No,
                    ContainsWords(line, " cascade")
                        ? "removes the table and its rows, and with CASCADE every object that depends on it"
                        : "removes the table and its rows");
            }

            yield break;
        }

        if (StartsWithWords(line, "alter table "))
        {
            foreach (TableLockStatement statement in ClassifyAlter(line, created))
            {
                yield return statement;
            }

            yield break;
        }

        if (BlockingIndexTable(line) is { } indexed && !created.Contains(Normalize(indexed)))
        {
            yield return LockOf(
                BuildsIndexKind,
                line,
                indexed,
                isIndex: false,
                TableLockMode.Share,
                TableRewrite.No,
                Normalize(indexed).Contains(TagTablePrefix, StringComparison.Ordinal)
                    ? "reads the whole table to build the index; every append of an event carrying the tag waits until it finishes"
                    : "reads the whole table to build the index; inserts, updates and deletes wait until it finishes, reads go on");
        }
    }

    private static IEnumerable<TableLockStatement> ClassifyAlter(string line, HashSet<string> created)
    {
        IReadOnlyList<string> segments = SplitStatements(line);

        if (segments.Count == 0 || AlterTarget(segments[0]) is not { } target)
        {
            yield break;
        }

        string key = Normalize(target.Table);

        // Marten's backfill: "update <this table> set …" in the same command, which runs under the lock the alter took.
        bool backfills = segments.Skip(1).Any(segment =>
            After(segment, "update ") is { } updated && FirstToken(updated) is { } updatedTable && Normalize(updatedTable) == key);

        foreach (TableLockStatement statement in ClassifyAlterCore(line, segments, target, created, backfills))
        {
            // Whatever the alter is, a backfill in the same command updates every row of the table while the lock is held.
            yield return backfills && statement.Rewrite != TableRewrite.Yes && statement.Kind != ReferencedByForeignKeyKind
                ? statement with
                {
                    Rewrite = TableRewrite.Yes,
                    Effect = statement.Effect + "; the same command then updates every row of the table while it holds the lock",
                }
                : statement;
        }
    }

    private static IEnumerable<TableLockStatement> ClassifyAlterCore(
        string line,
        IReadOnlyList<string> segments,
        (string Table, string Remainder) target,
        HashSet<string> created,
        bool backfills)
    {
        (string table, string rest) = target;
        string key = Normalize(table);
        bool isNew = created.Contains(key);

        // A new table has nothing to wait for, but its foreign key to an existing table validates against - and locks - that table.
        if (isNew && !(StartsWithWords(rest, "add ") && ContainsWords(rest, "foreign key")))
        {
            yield break;
        }

        if (isNew)
        {
            if (After(rest, " references ", anywhere: true) is { } newReferenced && FirstToken(newReferenced) is { } newReferencedTable
                && !created.Contains(Normalize(newReferencedTable)))
            {
                yield return LockOf(
                    ReferencedByForeignKeyKind,
                    line,
                    newReferencedTable,
                    isIndex: false,
                    TableLockMode.ShareRowExclusive,
                    TableRewrite.No,
                    "is locked against writes while a new table's key to it is validated, reads go on");
            }

            yield break;
        }

        if (StartsWithWords(rest, "drop column "))
        {
            string column = ColumnOf(rest["drop column ".Length..]);

            // A changed duplicated field: drop the column, add it again, fill it (DuplicatedFieldColumn.AlterColumnTypeSql).
            bool readded = segments.Skip(1).Any(segment =>
                AlterTarget(segment) is { } other && Normalize(other.Table) == key
                && StartsWithWords(other.Remainder, "add column ") && ColumnOf(other.Remainder["add column ".Length..]) == column);

            yield return readded
                ? LockOf(
                    ChangesColumnTypeKind,
                    line,
                    table,
                    isIndex: false,
                    TableLockMode.AccessExclusive,
                    backfills ? TableRewrite.Yes : TableRewrite.No,
                    backfills
                        ? "drops the column, adds it again and updates every row to fill it while it holds the lock"
                        : "drops the column and adds it again, empty")
                : LockOf(
                    DropsColumnKind,
                    line,
                    table,
                    isIndex: false,
                    TableLockMode.AccessExclusive,
                    TableRewrite.No,
                    "marks the column dropped without rewriting the table; its data is gone");
        }
        else if (StartsWithWords(rest, "drop constraint "))
        {
            yield return LockOf(
                DropsConstraintKind,
                line,
                table,
                isIndex: false,
                TableLockMode.AccessExclusive,
                TableRewrite.No,
                ContainsWords(rest, " cascade")
                    ? "drops the constraint and its index, and with CASCADE the foreign keys that reference it (the script adds them back)"
                    : "drops the constraint, and its index if it has one");
        }
        else if (StartsWithWords(rest, "alter column ") && ContainsWords(rest, " set not null"))
        {
            yield return LockOf(
                SetsNotNullKind,
                line,
                table,
                isIndex: false,
                TableLockMode.AccessExclusive,
                TableRewrite.No,
                "scans the whole table to check that no row is null while it holds the lock");
        }
        else if (StartsWithWords(rest, "alter column ") && ContainsWords(rest, " type "))
        {
            yield return LockOf(
                ChangesColumnTypeKind,
                line,
                table,
                isIndex: false,
                TableLockMode.AccessExclusive,
                TableRewrite.Maybe,
                "converts every row to the new type, which fails on a value that does not fit; the table's indexes are rebuilt with it");
        }
        else if (StartsWithWords(rest, "add ") && ContainsWords(rest, "foreign key"))
        {
            yield return LockOf(
                AddsForeignKeyKind,
                line,
                table,
                isIndex: false,
                TableLockMode.ShareRowExclusive,
                TableRewrite.No,
                "scans the table to validate the key: inserts, updates and deletes on it wait, reads go on");

            if (After(rest, " references ", anywhere: true) is { } referenced && FirstToken(referenced) is { } referencedTable
                && !created.Contains(Normalize(referencedTable)))
            {
                yield return LockOf(
                    ReferencedByForeignKeyKind,
                    line,
                    referencedTable,
                    isIndex: false,
                    TableLockMode.ShareRowExclusive,
                    TableRewrite.No,
                    "is locked against writes while the key is validated, reads go on");
            }
        }
        else if (StartsWithWords(rest, "add ") && ContainsWords(rest, "primary key"))
        {
            yield return LockOf(
                AddsPrimaryKeyKind,
                line,
                table,
                isIndex: false,
                TableLockMode.AccessExclusive,
                TableRewrite.No,
                "reads every row to build the key's unique index while holding the lock");
        }
        else if (StartsWithWords(rest, "add column ") && backfills)
        {
            yield return LockOf(
                AddsDuplicatedColumnKind,
                line,
                table,
                isIndex: false,
                TableLockMode.AccessExclusive,
                TableRewrite.Yes,
                "updates every row to fill the duplicated column while it holds the lock");
        }
        else if (StartsWithWords(rest, "add column ") && ContainsWords(rest, " default ") && VolatileDefault.IsMatch(rest))
        {
            yield return LockOf(
                AddsVolatileColumnKind,
                line,
                table,
                isIndex: false,
                TableLockMode.AccessExclusive,
                TableRewrite.Yes,
                "evaluates the default for every row, which rewrites the table while it holds the lock");
        }
        else if (StartsWithWords(rest, "rename constraint "))
        {
            yield return LockOf(
                RenamesConstraintKind,
                line,
                table,
                isIndex: false,
                TableLockMode.AccessExclusive,
                TableRewrite.No,
                "changes the constraint's name only, but waits for the lock like any other ALTER TABLE");
        }
        else
        {
            // The catch-all: whatever else Weasel or Marten alters. Most forms take ACCESS EXCLUSIVE; whether one rewrites is not judged.
            yield return LockOf(
                AltersTableKind,
                line,
                table,
                isIndex: false,
                TableLockMode.AccessExclusive,
                TableRewrite.Unknown,
                "is an ALTER TABLE form the studio has no rule for: most take ACCESS EXCLUSIVE, and it does not judge whether this one rewrites the table");
        }
    }

    /// <summary>The table an <c>ALTER TABLE</c> segment is about and what follows its name, or <see langword="null" />.</summary>
    private static (string Table, string Remainder)? AlterTarget(string segment)
    {
        if (After(segment, "alter table ") is not { } rest)
        {
            return null;
        }

        if (StartsWithWords(rest, "if exists "))
        {
            rest = rest["if exists ".Length..].TrimStart();
        }

        if (StartsWithWords(rest, "only "))
        {
            rest = rest["only ".Length..].TrimStart();
        }

        if (FirstToken(rest) is not { } table)
        {
            return null;
        }

        return (table, rest[Math.Min(table.Length, rest.Length)..].TrimStart());
    }

    /// <summary>A column name read from the text after <c>column </c>, folded as Postgres folds it.</summary>
    private static string ColumnOf(string text)
    {
        text = text.TrimStart();

        if (StartsWithWords(text, "if exists ") || StartsWithWords(text, "if not exists "))
        {
            text = text[(text.IndexOf("exists ", StringComparison.OrdinalIgnoreCase) + "exists ".Length)..].TrimStart();
        }

        return FirstToken(text) is { } token ? Normalize(token) : string.Empty;
    }

    private static TableLockStatement LockOf(
        string kind,
        string line,
        string relation,
        bool isIndex,
        TableLockMode mode,
        TableRewrite rewrite,
        string effect,
        bool isFunction = false)
    {
        string[] parts = Normalize(relation).Split(PartSeparator);
        string? schema = parts.Length >= 2 ? parts[^2] : null;

        return new TableLockStatement(kind, Clamp(line), schema, parts[^1], isIndex, mode, rewrite, effect, isFunction);
    }
}
