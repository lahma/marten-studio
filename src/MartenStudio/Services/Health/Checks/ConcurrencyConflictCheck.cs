using System.Text.RegularExpressions;

namespace MartenStudio.Services.Health.Checks;

/// <summary>Which of Marten's guarded writes a statement is.</summary>
internal enum GuardedWriteKind
{
    /// <summary>A document upsert whose <c>on conflict … do update</c> is guarded by the version it expects.</summary>
    Upsert,

    /// <summary>A document update guarded by the version it expects.</summary>
    Update,

    /// <summary>A rich append's <c>update mt_streams set version = $1 where … and version = $3</c>.</summary>
    StreamVersion,
}

/// <summary>One guarded write T22 recognised, and the store table it writes.</summary>
/// <param name="Statement">The statement.</param>
/// <param name="Kind">Which write it is.</param>
/// <param name="Table">The visible table it writes.</param>
internal sealed record GuardedWrite(ObservedStatement Statement, GuardedWriteKind Kind, MartenTable Table);

/// <summary>What T22 reads.</summary>
/// <param name="Read">The statement checks' shared read.</param>
/// <param name="Writes">The guarded writes among its statements, on the store's visible tables.</param>
internal sealed record ConcurrencyFacts(StatementRead Read, IReadOnlyList<GuardedWrite> Writes);

/// <summary>
/// T22: how often a document type's - or the event store's - guarded writes find that somebody else wrote first.
/// </summary>
/// <remarks>
/// <para>
/// <b>The rate is <c>1 − rows / calls</c> of the guarded writes.</b> Marten writes a document whose type uses
/// optimistic concurrency (or numeric revisions) with
/// <c>insert into … on conflict (…) do update set … where {table}.mt_version = $n returning mt_version</c>, or with
/// <c>update … where id = $n and mt_version = $m returning mt_version</c>; and a rich append checks the stream's
/// expected version with <c>update {schema}.mt_streams set version = $1 where id = $2 and version = $3 … returning
/// version</c>. Each returns its row when the version matched and none when it did not, so the calls that returned
/// nothing are the conflicts. Verified against the Marten sources at both ends: <c>DocumentStorageDescriptorBuilder</c>
/// (the closed-shape write path, Marten's only document write path since #4404) is byte-identical at V9.39.0 and
/// V9.50.2, and so is <c>PostgresEventStoreDialect.BuildUpdateStreamVersionCommandConfigurer</c>.
/// </para>
/// <para>
/// <b>Matched to its table by name, against the store's visible tables.</b> The target of the <c>insert into</c> or
/// <c>update</c> must be one of the tables the statement names (<see cref="StatementReads.Named" />), so a hidden
/// type's writes are never counted and never named. The rate is per table - every guarded statement of one document
/// type summed - over at least <see cref="MinCalls" /> calls, across the window the statement history holds.
/// </para>
/// <para>
/// <b>What it cannot see.</b> <c>pg_stat_statements</c> records a statement only when it completes. A quick append's
/// conflict is a unique violation that fails its statement, so it is never counted here; E02 counts the tombstones a
/// failed rich append leaves, and C08 the rollbacks.
/// </para>
/// </remarks>
[HealthCheck("T22", HealthArea.Tables, HealthCostTier.Statements, "Optimistic-concurrency conflicts", Gate = HealthGate.Activity)]
internal sealed partial class ConcurrencyConflictCheck : HealthCheck<ConcurrencyFacts>
{
    /// <summary>The fewest guarded calls in the window a rate is judged on.</summary>
    public const int MinCalls = 1000;

    /// <summary>A conflict rate at or above this is a Warning; anything above zero below it is Info.</summary>
    public const double WarningRate = 0.05;

    /// <summary>The series of a guarded statement's calls.</summary>
    internal const string CallsSeries = "statements.guarded-calls:";

    /// <summary>The series of a guarded statement's rows.</summary>
    internal const string RowsSeries = "statements.guarded-rows:";

    /// <inheritdoc />
    public override async Task<ConcurrencyFacts> GatherAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        StatementRead read = await StatementReads.ReadAsync(context).ConfigureAwait(false);
        return new ConcurrencyFacts(read, Recognise(read));
    }

    /// <summary>The guarded writes among <paramref name="read" />'s statements.</summary>
    internal static IReadOnlyList<GuardedWrite> Recognise(StatementRead read)
    {
        ArgumentNullException.ThrowIfNull(read);

        List<GuardedWrite> writes = [];

        foreach (ObservedStatement statement in read.Statements)
        {
            if (statement is { Text: { } text, TextTruncated: false } && Classify(text) is { } shape
                && statement.Tables.FirstOrDefault(x => IsTarget(x, shape.Target)) is { } table)
            {
                writes.Add(new GuardedWrite(statement, shape.Kind, table));
            }
        }

        return writes;
    }

    /// <inheritdoc />
    public override IEnumerable<HealthSamplePart> Sample(ConcurrencyFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        foreach (GuardedWrite write in facts.Writes)
        {
            yield return new HealthSamplePart(CallsSeries + write.Statement.Key, write.Statement.Counters.Calls);
            yield return new HealthSamplePart(RowsSeries + write.Statement.Key, write.Statement.Counters.Rows);
        }
    }

    /// <inheritdoc />
    public override HealthVerdict Evaluate(ConcurrencyFacts facts, HealthEvaluation evaluation)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(evaluation);

        List<HealthFinding> findings = [];
        List<string> quiet = [];

        foreach (IGrouping<string, GuardedWrite> table in facts.Writes.GroupBy(static x => x.Table.Key, StringComparer.OrdinalIgnoreCase))
        {
            List<(GuardedWrite Write, StatementWindow Window)> windows =
                [.. table.Select(x => (x, WindowOf(facts.Read, x, evaluation.History)))];

            // One window for the table: the advisor's, when it has watched every statement; else every one since it began.
            bool watched = windows.All(static x => x.Window.Watched);
            if (!watched)
            {
                windows = [.. windows.Select(x => (x.Write, WindowOf(facts.Read, x.Write, HealthHistoryView.Empty)))];
            }

            double calls = windows.Sum(static x => x.Window.Calls);
            double rows = windows.Sum(static x => Math.Min(x.Window.Rows, x.Window.Calls));
            MartenTable written = table.First().Table;

            if (calls < MinCalls)
            {
                quiet.Add(Describe(written) + ": " + TableText.Count(calls) + " guarded writes, fewer than the " + TableText.Count(MinCalls) + " a rate is judged on");
                continue;
            }

            double conflicts = calls - rows;
            double rate = Math.Clamp(conflicts / calls, 0, 1);

            if (conflicts <= 0)
            {
                quiet.Add(Describe(written) + ": " + TableText.Count(calls) + " guarded writes, none conflicted");
                continue;
            }

            StatementWindow span = windows
                .Select(static x => x.Window)
                .OrderByDescending(static x => x.Span ?? TimeSpan.Zero)
                .First();

            findings.Add(Finding(facts.Read, written, windows, calls, conflicts, rate, span with { Calls = calls, Rows = rows }, evaluation));
        }

        if (findings.Count > 0)
        {
            return new HealthVerdict(findings);
        }

        return HealthVerdict.Quiet(quiet.Count == 0
            ? "No guarded write of the store's - an optimistic-concurrency upsert or update, or a rich append's expected-version "
              + "check - is among the statements pg_stat_statements kept."
            : string.Join("; ", quiet) + ".");
    }

    /// <summary>The window of one guarded statement: its own samples, or its totals since its counters began.</summary>
    internal static StatementWindow WindowOf(StatementRead read, GuardedWrite write, HealthHistoryView history) =>
        StatementWindows.Of(
            read,
            history,
            (CallsSeries + write.Statement.Key, CallsSeries + write.Statement.Key, RowsSeries + write.Statement.Key),
            write.Statement.Counters.Calls,
            write.Statement.Counters.Calls,
            write.Statement.Counters.Rows,
            StatementWindows.Since(read, write.Statement));

    private static HealthFinding Finding(
        StatementRead read,
        MartenTable table,
        IReadOnlyList<(GuardedWrite Write, StatementWindow Window)> windows,
        double calls,
        double conflicts,
        double rate,
        StatementWindow span,
        HealthEvaluation evaluation)
    {
        bool streams = table.Kind == MartenTableKind.EventStore;

        string statements = string.Join("; ", windows
            .GroupBy(static x => x.Write.Kind)
            .OrderBy(static x => x.Key)
            .Select(static x => KindName(x.Key) + " " + TableText.Count(x.Sum(static y => y.Window.Calls)) + " calls, "
                + TableText.Count(x.Sum(static y => Math.Max(0, y.Window.Calls - y.Window.Rows))) + " conflicts"));

        return new HealthFinding
        {
            Code = "T22",
            Severity = rate >= WarningRate ? HealthSeverity.Warning : HealthSeverity.Info,
            Subject = table.Key,
            Title = streams
                ? TableText.Percent(rate) + " of the event store's expected-version appends found the stream had moved on"
                : TableText.Percent(rate) + " of the " + table.TypeName + " collection's guarded writes found a newer version",
            WhyItMatters = streams
                ? "Each conflict is a SaveChangesAsync that threw EventStreamUnexpectedMaxEventIdException: two commands appended to one "
                  + "stream at once, and the one that lost had done its work for nothing - and, with a retry policy, does it again. A rate "
                  + "this high means streams that many writers touch together, and retries make it worse under load."
                : "Each conflict is a SaveChangesAsync that threw ConcurrencyException: two commands loaded the same document, both "
                  + "changed it, and the second to save found the first had already written. Its work is thrown away - and, with a retry "
                  + "policy, done again. A rate this high means documents that many writers update together, and retries make it worse "
                  + "under load.",
            Evidence =
            [
                new HealthEvidence("Guarded writes", TableText.Count(calls)),
                new HealthEvidence("Conflicts", TableText.Count(conflicts)),
                new HealthEvidence("Conflict rate", TableText.Percent(rate)),
                new HealthEvidence("Statements", statements),
                new HealthEvidence("Window", StatementWindows.Describe(span, read.ReadAt)),
            ],
            Caveat = StatementWindows.Caveat(read, span, evaluation.History)
                + " A statement is counted only when it completes: a quick append's conflict is a unique violation that fails its "
                + "statement, so it is never counted here - E02 counts the tombstones a failed rich append leaves, and C08 the rollbacks.",
            Remedy = streams
                ? new HealthRemedy(
                    "// Serialise the writers of a hot stream instead of letting them collide and retry:\n"
                    + "var stream = await session.Events.FetchForExclusiveWriting<Order>(orderId);",
                    "Modelling, not configuration. Keep aggregates small, so fewer commands touch the same stream; take the hot ones with "
                    + "FetchForExclusiveWriting, which locks the stream's mt_streams row until the session commits, so the second writer "
                    + "waits instead of failing; and give a long-lived, busy aggregate a stream per period - a day, a month - rather than "
                    + "one stream for ever.")
                : new HealthRemedy(
                    "// Fewer commands writing one " + table.TypeName + " at once: keep the document small, split out the part\n"
                    + "// every command changes, and serialise a hot aggregate's writers - for an event-sourced one:\n"
                    + "var stream = await session.Events.FetchForExclusiveWriting<" + table.TypeName + ">(id);",
                    "Modelling, not configuration. Optimistic concurrency is doing its job - every conflict was caught - and the remedy is "
                    + "fewer writers on one document: a smaller aggregate, the busy part in a document of its own, or, for an aggregate built "
                    + "from events, FetchForExclusiveWriting so the second writer waits instead of failing, and a stream per period for a "
                    + "long-lived, busy one."),
            DocumentTypes = table.DocumentTypes,
        };
    }

    private static string KindName(GuardedWriteKind kind) => kind switch
    {
        GuardedWriteKind.Upsert => "guarded upsert",
        GuardedWriteKind.Update => "guarded update",
        _ => "stream version check",
    };

    private static string Describe(MartenTable table) => table.Kind == MartenTableKind.EventStore
        ? "the event store's streams"
        : "the " + table.TypeName + " collection";

    /// <summary>Whether <paramref name="table" /> is what a write's target name, qualified or bare, names.</summary>
    private static bool IsTarget(MartenTable table, string target)
    {
        string[] parts = target.Split('.');
        string name = Unquote(parts[^1]);

        if (!string.Equals(table.Table, name, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return parts.Length == 1 || string.Equals(table.Schema, Unquote(parts[^2]), StringComparison.OrdinalIgnoreCase);
    }

    private static string Unquote(string part) =>
        part.Length >= 2 && part[0] == '"' && part[^1] == '"' ? part[1..^1].Replace("\"\"", "\"", StringComparison.Ordinal) : part;

    /// <summary>
    /// Which guarded write <paramref name="text" /> is - by Marten's own shapes, as <c>pg_stat_statements</c> normalises
    /// them - and the name of the table it writes; or <see langword="null" /> for any other statement.
    /// </summary>
    internal static (GuardedWriteKind Kind, string Target)? Classify(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        string statement = text.Trim().TrimEnd(';').TrimEnd();

        if (Insert().Match(statement) is { Success: true } insert)
        {
            if (!ReturningVersion().IsMatch(statement))
            {
                return null;
            }

            int set = statement.IndexOf(" do update set ", StringComparison.OrdinalIgnoreCase);
            if (set < 0)
            {
                return null;
            }

            string guard = TopLevelWhere(statement[(set + " do update set ".Length)..]);
            return guard.Contains("mt_version", StringComparison.OrdinalIgnoreCase) ? (GuardedWriteKind.Upsert, insert.Groups["table"].Value) : null;
        }

        if (Update().Match(statement) is { Success: true } update)
        {
            string target = update.Groups["table"].Value;
            string after = statement[(update.Index + update.Length)..];

            if (Unquote(target.Split('.')[^1]).Equals("mt_streams", StringComparison.OrdinalIgnoreCase))
            {
                return ReturningStreamVersion().IsMatch(statement) && StreamGuard().IsMatch(TopLevelWhere(after))
                    ? (GuardedWriteKind.StreamVersion, target)
                    : null;
            }

            return ReturningVersion().IsMatch(statement) && TopLevelWhere(after).Contains("mt_version", StringComparison.OrdinalIgnoreCase)
                ? (GuardedWriteKind.Update, target)
                : null;
        }

        return null;
    }

    /// <summary>
    /// What follows the last <c>where</c> outside every parenthesis and quote in <paramref name="text" />, up to its
    /// <c>returning</c> - a guard, not a subquery's own <c>where</c> - or nothing.
    /// </summary>
    internal static string TopLevelWhere(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        int depth = 0;
        int where = -1;
        char quote = '\0';

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];

            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            switch (c)
            {
                case '"' or '\'':
                    quote = c;
                    break;
                case '(':
                    depth++;
                    break;
                case ')':
                    depth--;
                    break;
                default:
                    if (depth == 0 && IsKeywordAt(text, i, "where"))
                    {
                        where = i;
                    }

                    break;
            }
        }

        if (where < 0)
        {
            return string.Empty;
        }

        string guard = text[(where + "where".Length)..];
        int returning = guard.LastIndexOf("returning", StringComparison.OrdinalIgnoreCase);
        return returning < 0 ? guard : guard[..returning];
    }

    private static bool IsKeywordAt(string text, int index, string keyword) =>
        index + keyword.Length <= text.Length
        && string.Compare(text, index, keyword, 0, keyword.Length, StringComparison.OrdinalIgnoreCase) == 0
        && (index == 0 || !IsNameCharacter(text[index - 1]))
        && (index + keyword.Length == text.Length || !IsNameCharacter(text[index + keyword.Length]));

    private static bool IsNameCharacter(char c) => char.IsLetterOrDigit(c) || c is '_' or '$' or '.';

    [GeneratedRegex(@"^insert\s+into\s+(?<table>(?:""(?:[^""]|"""")+""|[A-Za-z_][A-Za-z0-9_$]*)(?:\s*\.\s*(?:""(?:[^""]|"""")+""|[A-Za-z_][A-Za-z0-9_$]*))?)\s*\(",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Insert();

    [GeneratedRegex(@"^update\s+(?<table>(?:""(?:[^""]|"""")+""|[A-Za-z_][A-Za-z0-9_$]*)(?:\s*\.\s*(?:""(?:[^""]|"""")+""|[A-Za-z_][A-Za-z0-9_$]*))?)\s+set\s",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Update();

    [GeneratedRegex(@"\sreturning\s+""?mt_version""?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ReturningVersion();

    [GeneratedRegex(@"\sreturning\s+""?version""?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ReturningStreamVersion();

    [GeneratedRegex(@"\band\s+""?version""?\s*=\s*\$[0-9]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex StreamGuard();
}
