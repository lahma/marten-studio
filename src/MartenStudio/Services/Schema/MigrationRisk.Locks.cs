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

    /// <summary>Conflicts with every other lock, reads included.</summary>
    AccessExclusive,
}

/// <summary>Whether a statement writes the whole table again.</summary>
internal enum TableRewrite
{
    /// <summary>It does not.</summary>
    No,

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
internal sealed record TableLockStatement(
    string Kind,
    string Statement,
    string? Schema,
    string Name,
    bool IsIndex,
    TableLockMode Lock,
    TableRewrite Rewrite,
    string Effect)
{
    /// <summary>The relation as a person reads it: <c>schema.name</c>, or the bare name.</summary>
    public string Display => Schema is null ? Name : Schema + "." + Name;

    /// <summary>The lock as PostgreSQL spells it.</summary>
    public string LockName => Lock switch
    {
        TableLockMode.AccessShare => "ACCESS SHARE",
        TableLockMode.ShareUpdateExclusive => "SHARE UPDATE EXCLUSIVE",
        TableLockMode.Share => "SHARE",
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
/// decides nothing and refuses nothing. It names the statements <see cref="Find" /> and <see cref="Notes" /> name on a table
/// the script does not itself create, and the one Weasel writes for a changed or added primary key (<c>alter table … add
/// CONSTRAINT … PRIMARY KEY</c>), which neither names. A relation is "existing" until the script creates it, in order: the partition
/// rebuild's <c>create table s.t_temp as select * from s.t</c>, <c>drop table s.t cascade</c> and <c>CREATE TABLE s.t</c> are
/// about <c>s.t</c> before the create and about nothing after it, and <c>s.t_temp</c> is the script's own throughout.
/// </para>
/// <para>
/// <b>The locks are PostgreSQL's, not Weasel's.</b> A plain <c>CREATE INDEX</c> takes <c>SHARE</c>, so writers wait and readers do
/// not; <c>CREATE</c> and <c>DROP INDEX CONCURRENTLY</c> take <c>SHARE UPDATE EXCLUSIVE</c> and block neither; every <c>ALTER
/// TABLE</c> form here, and a plain <c>DROP INDEX</c> or <c>DROP TABLE</c>, takes <c>ACCESS EXCLUSIVE</c> on the table. (Weasel's
/// remark on <c>PostgresqlMigrator.BuildIndexesConcurrentlyOnAlter</c> calls a plain build <c>ACCESS EXCLUSIVE</c>; the
/// documentation of <c>CREATE INDEX</c> says <c>SHARE</c>, and so does the existing <see cref="BlockingIndexBuildKind" />.)
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

    /// <summary>What an <c>ACCESS EXCLUSIVE</c> statement does to the sessions that arrive while it waits.</summary>
    internal const string AccessExclusiveAdvice =
        "An ACCESS EXCLUSIVE statement waits for every session using the table to finish, and while it waits it queues every "
        + "reader and writer that arrives behind it - one long transaction on the table can stop the application. Apply at a quiet "
        + "time, or run the migration from the host's own migration path with SET lock_timeout (for example '5s'), so it gives up "
        + "rather than queue. This screen's apply sets no lock_timeout.";

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
            string line = lines[i];

            if (IsPartitionIndexResume(lines, i))
            {
                continue;
            }

            if (After(line, "create table ") is { } creating)
            {
                if (StartsWithWords(creating, "if not exists "))
                {
                    creating = creating["if not exists ".Length..].TrimStart();
                }

                if (FirstToken(creating) is not { } target)
                {
                    continue;
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

                created.Add(Normalize(target));
                continue;
            }

            if (Classify(line, created) is { } statement)
            {
                found.Add(statement);
            }
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

    private static TableLockStatement? Classify(string line, HashSet<string> created)
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

            return FirstToken(rest) is { } index
                ? LockOf(
                    MigrationRisk.DropsIndexKind,
                    line,
                    index,
                    isIndex: true,
                    concurrently ? TableLockMode.ShareUpdateExclusive : TableLockMode.AccessExclusive,
                    TableRewrite.No,
                    concurrently
                        ? "waits for the transactions that could use the index, then drops it; reads and writes go on"
                        : "takes the lock on the index's table and drops the index")
                : null;
        }

        if (StartsWithWords(line, "drop table "))
        {
            string rest = line["drop table ".Length..].TrimStart();

            if (StartsWithWords(rest, "if exists "))
            {
                rest = rest["if exists ".Length..].TrimStart();
            }

            return FirstToken(rest) is { } table && !created.Contains(Normalize(table))
                ? LockOf(
                    MigrationRisk.DropsTableKind,
                    line,
                    table,
                    isIndex: false,
                    TableLockMode.AccessExclusive,
                    TableRewrite.No,
                    ContainsWords(line, " cascade")
                        ? "removes the table and its rows, and with CASCADE every object that depends on it"
                        : "removes the table and its rows")
                : null;
        }

        if (StartsWithWords(line, "alter table "))
        {
            return ClassifyAlter(line, created);
        }

        if (BlockingIndexTable(line) is { } indexed && !created.Contains(Normalize(indexed)))
        {
            return LockOf(
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

        return null;
    }

    private static TableLockStatement? ClassifyAlter(string line, HashSet<string> created)
    {
        string rest = line["alter table ".Length..].TrimStart();

        if (StartsWithWords(rest, "if exists "))
        {
            rest = rest["if exists ".Length..].TrimStart();
        }

        if (StartsWithWords(rest, "only "))
        {
            rest = rest["only ".Length..].TrimStart();
        }

        if (FirstToken(rest) is not { } table || created.Contains(Normalize(table)))
        {
            return null;
        }

        string? destructive = KindOf(line);

        return destructive switch
        {
            MigrationRisk.DropsColumnKind => LockOf(
                destructive,
                line,
                table,
                isIndex: false,
                TableLockMode.AccessExclusive,
                TableRewrite.No,
                "marks the column dropped without rewriting the table; its data is gone"),
            MigrationRisk.DropsConstraintKind => LockOf(
                destructive,
                line,
                table,
                isIndex: false,
                TableLockMode.AccessExclusive,
                TableRewrite.No,
                ContainsWords(line, " cascade")
                    ? "drops the constraint and its index, and with CASCADE the foreign keys that reference it (the script adds them back)"
                    : "drops the constraint, and its index if it has one"),
            MigrationRisk.ChangesColumnTypeKind => LockOf(
                destructive,
                line,
                table,
                isIndex: false,
                TableLockMode.AccessExclusive,
                TableRewrite.Maybe,
                "converts every row to the new type, which fails on a value that does not fit; the table's indexes are rebuilt with it"),
            _ when ContainsWords(line, " add ") && ContainsWords(line, "primary key") => LockOf(
                AddsPrimaryKeyKind,
                line,
                table,
                isIndex: false,
                TableLockMode.AccessExclusive,
                TableRewrite.No,
                "reads every row to build the key's unique index while holding the lock"),
            _ => null,
        };
    }

    private static TableLockStatement LockOf(
        string kind,
        string line,
        string relation,
        bool isIndex,
        TableLockMode mode,
        TableRewrite rewrite,
        string effect)
    {
        string[] parts = Normalize(relation).Split(PartSeparator);
        string? schema = parts.Length >= 2 ? parts[^2] : null;

        return new TableLockStatement(kind, Clamp(line), schema, parts[^1], isIndex, mode, rewrite, effect);
    }
}
