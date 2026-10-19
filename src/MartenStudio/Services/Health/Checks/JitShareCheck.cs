using System.Globalization;

namespace MartenStudio.Services.Health.Checks;

/// <summary>What T23 reads.</summary>
/// <param name="Read">The statement checks' shared read.</param>
internal sealed record JitFacts(StatementRead Read);

/// <summary>T23: statements of the store whose time goes to compiling them, rather than running them.</summary>
/// <remarks>
/// <para>
/// <b>JIT time over a fifth of the statement's time, across <see cref="MinCalls" /> calls or more.</b> PostgreSQL compiles a
/// query to machine code when its estimated cost passes <c>jit_above_cost</c>, and compiles it again on every execution -
/// the code is not kept. That pays for itself in a long analytical query and costs a short one more than it saves; Marten's
/// statements are short, and a large table's cost estimates push them over the line. The JIT time is what
/// <c>pg_stat_statements</c> records from extension 1.10 (PostgreSQL 15): generation, inlining, optimisation and emission,
/// and on 17 deforming, all of which fall inside the statement's execution time.
/// </para>
/// <para>
/// <b>The remedy is the store's own connections.</b> <c>opts.ConfigureNpgsqlDataSourceBuilder</c> (on <c>StoreOptions</c>
/// at Marten 9.39.0 and 9.50.2) applies to every data source Marten builds - tenant databases included - and
/// <c>NpgsqlConnectionStringBuilder.Options</c> (Npgsql 9.0.4) sets a server setting for each connection, so
/// <c>-c jit=off</c> turns JIT off for the store and nobody else.
/// </para>
/// </remarks>
[HealthCheck("T23", HealthArea.Tables, HealthCostTier.Statements, "JIT compilation share", Gate = HealthGate.Activity)]
internal sealed class JitShareCheck : HealthCheck<JitFacts>
{
    /// <summary>The fewest calls in the window a statement's JIT share is judged on.</summary>
    public const int MinCalls = 100;

    /// <summary>A statement whose JIT time is more than this share of its execution time is a Warning.</summary>
    public const double WarningShare = 0.20;

    /// <summary>The most statements the finding names.</summary>
    public const int MaxNamed = 3;

    /// <summary>The series of a statement's calls, as T23 samples it.</summary>
    internal const string CallsSeries = "statements.jit-calls:";

    /// <summary>The series of a statement's execution time, in milliseconds, as T23 samples it.</summary>
    internal const string ExecSeries = "statements.jit-exec-ms:";

    /// <summary>The series of a statement's JIT time, in milliseconds.</summary>
    internal const string JitSeries = "statements.jit-ms:";

    /// <summary>The remedy's line.</summary>
    internal const string RemedyCode =
        "opts.ConfigureNpgsqlDataSourceBuilder(b =>\n"
        + "    b.ConnectionStringBuilder.Options = ((b.ConnectionStringBuilder.Options ?? string.Empty) + \" -c jit=off\").Trim());";

    /// <inheritdoc />
    public override async Task<JitFacts> GatherAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        StatementRead read = await StatementReads.ReadAsync(context).ConfigureAwait(false);

        if (!read.Extension.HasJit)
        {
            throw new HealthCheckUnavailableException(
                "pg_stat_statements records JIT time from version 1.10 (PostgreSQL 15), and this database has " + read.Extension.Version
                + ", so there is no JIT time to judge.");
        }

        return new JitFacts(read);
    }

    /// <inheritdoc />
    public override IEnumerable<HealthSamplePart> Sample(JitFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        foreach (ObservedStatement statement in Candidates(facts.Read))
        {
            yield return new HealthSamplePart(CallsSeries + statement.Key, statement.Counters.Calls);
            yield return new HealthSamplePart(ExecSeries + statement.Key, statement.Counters.TotalExecMs);
            yield return new HealthSamplePart(JitSeries + statement.Key, statement.Counters.JitMs ?? 0);
        }
    }

    /// <inheritdoc />
    public override HealthVerdict Evaluate(JitFacts facts, HealthEvaluation evaluation)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(evaluation);

        List<(ObservedStatement Statement, StatementWindow Window, double JitMs, double Share)> over = [];

        foreach (ObservedStatement statement in Candidates(facts.Read))
        {
            (StatementWindow window, double jit) = WindowOf(facts.Read, statement, evaluation.History);

            if (window.Calls >= MinCalls && window.ExecMs > 0 && jit / window.ExecMs > WarningShare)
            {
                over.Add((statement, window, jit, Math.Min(1, jit / window.ExecMs)));
            }
        }

        if (over.Count == 0)
        {
            return HealthVerdict.Quiet(
                "No statement of the store's own role on its tables spent more than " + TableText.Percent(WarningShare)
                + " of its time compiling, over " + TableText.Count(MinCalls) + " calls or more.");
        }

        over.Sort(static (a, b) => b.JitMs.CompareTo(a.JitMs));

        List<HealthEvidence> evidence =
        [
            new("Statements over " + TableText.Percent(WarningShare), TableText.Count(over.Count)),
            new("JIT time", StatementWindows.Milliseconds(over.Sum(static x => x.JitMs)) + " of "
                + StatementWindows.Milliseconds(over.Sum(static x => x.Window.ExecMs)) + " of their execution time"),
        ];

        for (int i = 0; i < Math.Min(over.Count, MaxNamed); i++)
        {
            (ObservedStatement statement, StatementWindow window, double jit, double share) = over[i];
            string label = "Statement " + (i + 1).ToString(CultureInfo.InvariantCulture);

            evidence.Add(statement.Evidence(label));
            evidence.Add(new HealthEvidence(label + " JIT", TableText.Percent(share) + " of its " + StatementWindows.Milliseconds(window.ExecMs)
                + " over " + TableText.Count(window.Calls) + " calls (" + StatementWindows.Milliseconds(jit / window.Calls) + " a call)"));
        }

        StatementWindow first = over[0].Window;
        evidence.Add(new HealthEvidence("Window", StatementWindows.Describe(first, facts.Read.ReadAt)));

        return HealthVerdict.Of(new HealthFinding
        {
            Code = "T23",
            Severity = HealthSeverity.Warning,
            Title = over.Count == 1
                ? "JIT compilation takes " + TableText.Percent(over[0].Share) + " of a store statement's time"
                : "JIT compilation takes more than " + TableText.Percent(WarningShare) + " of the time of " + TableText.Count(over.Count) + " store statements",
            WhyItMatters =
                "PostgreSQL compiles a statement to machine code when its estimated cost passes jit_above_cost, and compiles it again on "
                + "every execution: the compiled code is not kept. That pays off for a long analytical query and costs a short one more "
                + "than it saves - milliseconds of compiling for a statement that runs in less. Marten's statements are short, and a "
                + "large table's cost estimates are what push them over the line.",
            Evidence = evidence,
            Caveat = StatementWindows.Caveat(facts.Read, first, evaluation.History)
                + " JIT time is the generation, inlining, optimisation and emission (and, from PostgreSQL 17, deforming) times "
                + "pg_stat_statements records, all of them inside the statement's execution time.",
            Remedy = new HealthRemedy(
                RemedyCode,
                "Turns JIT off for the store's own connections only - every data source Marten builds, tenant databases included - and "
                + "leaves the server's setting alone for everybody else. A data source the host builds itself (UseNpgsqlDataSource) "
                + "takes the same Options on its own NpgsqlDataSourceBuilder. To keep JIT for the occasional long query, a DBA can "
                + "raise jit_above_cost for the store's role instead."),
            DocumentTypes = [.. over.SelectMany(static x => x.Statement.DocumentTypes).Distinct()],
            GatedSchemas = [.. over.SelectMany(static x => x.Statement.GatedSchemas).Distinct(StringComparer.Ordinal)],
        });
    }

    /// <summary>The statements T23 judges: the store's, with their text read and JIT time recorded.</summary>
    internal static IEnumerable<ObservedStatement> Candidates(StatementRead read) =>
        read.Statements.Where(static x => x.NamesStoreTable && x.IsWorkload && x.Counters.JitMs is not null);

    /// <summary>
    /// A statement's calls and execution time over the window, and its JIT time over the same window - the advisor's own
    /// when two statement checks have sampled it, its totals since its counters began otherwise.
    /// </summary>
    internal static (StatementWindow Window, double JitMs) WindowOf(StatementRead read, ObservedStatement statement, HealthHistoryView history)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(statement);
        ArgumentNullException.ThrowIfNull(history);

        // The window's third counter is the JIT time: the same window over three series of T23's own.
        StatementWindow window = StatementWindows.Of(
            read,
            history,
            (CallsSeries + statement.Key, ExecSeries + statement.Key, JitSeries + statement.Key),
            statement.Counters.Calls,
            statement.Counters.TotalExecMs,
            statement.Counters.JitMs ?? 0,
            StatementWindows.Since(read, statement));

        return (window, window.Rows);
    }
}
