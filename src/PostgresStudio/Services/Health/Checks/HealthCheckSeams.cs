using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Sources;

namespace PostgresStudio.Services.Health.Checks;

/// <summary>
/// How a studio words each table remedy the tables-and-indexes checks offer - one member per remedy shape (issue #8,
/// G12): MartenStudio's are <c>StoreOptions</c> lines, and a studio over plain data sources words the statement a DBA
/// would hand to whoever owns the schema. The one table-remedy seam: PS-1a's generic <c>IHealthRemedyProvider</c>
/// (storage parameter, add index, remove index) was folded into it in PS-1e, each of its shapes being one of these.
/// </summary>
/// <remarks>
/// <para>
/// MartenStudio answers a <c>StoreOptions</c> line for a document table (<c>opts.Schema.For&lt;T&gt;()…</c>), because the
/// next <c>CreateOrUpdate</c> resets whatever the configuration does not declare. A plain source answers the statement a
/// DBA would run - <c>ALTER TABLE … SET (autovacuum_…)</c>, <c>CREATE INDEX CONCURRENTLY …</c> - as text to hand to
/// whoever owns the schema, rendered under "For your own migrations - the studio never runs this".
/// </para>
/// <para>
/// <b>Why a member per shape.</b> The check knows what to suggest - remove this redundant index, leave room for HOT
/// updates - and the studio knows how its configuration says it. Two remedies that both remove an index say different
/// things about why and what else to keep, so the reason is the member, not a parameter the wording would have to
/// branch on.
/// </para>
/// <para>
/// <b>Every member is pure text.</b> It never runs what it words and never connects. It answers
/// <see langword="null" /> when there is nothing honest to suggest for the table - another source's, a library's, one
/// whose owner cannot be told, a server setting no line changes - and the finding then says what to do in words or not
/// at all. One that is a configuration line is refused DDL by <see cref="HealthRemedy" /> itself.
/// </para>
/// <para>
/// <b>SQL text is built where all SQL is built (AGENTS.md hard rule 4).</b> A remedy that is SQL is composed in, or by a
/// builder in, <c>Internal/Sql</c> - never by concatenation in an implementation of this or in a check - and every
/// identifier in it (schema, table, index, column) is quoted through <c>SqlIdentifier</c>, never interpolated raw: the
/// text is copied into a migration and run by a person with more rights than the studio, so a table name that is not
/// quoted is an injection the studio handed them. A value it suggests is a literal the builder formats, never a
/// visitor's input.
/// </para>
/// <para>
/// Handed to an evaluator in <see cref="HealthEvaluation.TableRemedies" />, read by the sweep from its own scope; a studio
/// that registers none offers no table remedy.
/// </para>
/// </remarks>
internal interface IHealthTableRemedies
{
    /// <summary>T01: an index for the member a query that reads the whole table filters, sorts or joins on.</summary>
    /// <param name="table">The table the sequential scans read.</param>
    HealthRemedy? IndexTheFilteredMember(StudioTable table);

    /// <summary>
    /// T02: an index for a member a statement filters on that the recorded text does not name - its key is a placeholder in
    /// <c>pg_stat_statements</c> - offered only if no index on it exists already.
    /// </summary>
    /// <param name="table">The table the statement reads whole.</param>
    HealthRemedy? IndexTheUnnamedMember(StudioTable table);

    /// <summary>T03: rebuilding an invalid index on an ordinary (not partitioned) table.</summary>
    /// <param name="table">The table.</param>
    /// <param name="index">The invalid index.</param>
    /// <param name="origin">Where it comes from.</param>
    HealthRemedy? RebuildInvalidIndex(StudioTable table, HealthIndexRow index, IndexOrigin origin);

    /// <summary>T04: removing an index another valid index already does the whole job of.</summary>
    /// <param name="table">The table.</param>
    /// <param name="index">The redundant index.</param>
    /// <param name="origin">Where it comes from.</param>
    /// <param name="coveredBy">The index that covers it.</param>
    HealthRemedy? RemoveRedundantIndex(StudioTable table, HealthIndexRow index, IndexOrigin origin, HealthIndexRow coveredBy);

    /// <summary>T05: removing an index nothing has scanned on a table that is written to a lot.</summary>
    /// <param name="table">The table.</param>
    /// <param name="index">The unused index.</param>
    /// <param name="origin">Where it comes from.</param>
    HealthRemedy? RemoveUnusedIndex(StudioTable table, HealthIndexRow index, IndexOrigin origin);

    /// <summary>T06: a fillfactor that leaves room on each page for an update to stay HOT.</summary>
    /// <param name="table">The table.</param>
    /// <param name="fillFactor">The fillfactor to suggest.</param>
    HealthRemedy? LeaveRoomForHotUpdates(StudioTable table, int fillFactor);

    /// <summary>
    /// T06: an index on a column of its own in place of an expression index over a column every write changes, so the other
    /// updates can stay HOT.
    /// </summary>
    /// <param name="table">The table.</param>
    HealthRemedy? IndexAColumnInPlaceOfAnExpression(StudioTable table);

    /// <summary>T07 and T08: switching autovacuum back on for a table whose own storage parameter switched it off.</summary>
    /// <param name="table">The table.</param>
    HealthRemedy? ReenableAutovacuum(StudioTable table);

    /// <summary>T07 and T08: one autovacuum storage parameter of a table, set to <paramref name="value" />.</summary>
    /// <param name="table">The table.</param>
    /// <param name="parameter">The storage parameter: <c>autovacuum_vacuum_scale_factor</c>.</param>
    /// <param name="value">Its suggested value, as SQL spells it.</param>
    /// <param name="explanation">What the setting changes, in words that hold for any source.</param>
    HealthRemedy? AutovacuumSetting(StudioTable table, string parameter, string value, string explanation);

    // ---- W5-V1 ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// T24: switching autovacuum back on for a table's TOAST table, whose own storage parameter
    /// (<c>toast.autovacuum_enabled = false</c>) switched it off - which Marten's <c>StorageParameter</c> does not manage
    /// (<c>toast.*</c> parameters are not supported, its own documentation says), so for a Marten table it is words, never a line.
    /// </summary>
    /// <param name="table">The table whose TOAST table it is.</param>
    HealthRemedy? ReenableToastAutovacuum(StudioTable table);
}

/// <summary>
/// What a studio can say about a sequential scan of one of its tables in a statement's plan (T02): what the scan's filter
/// reads, and which index would serve it.
/// </summary>
/// <remarks>
/// MartenStudio reads a document table's filter with its own index advisor - the Query page's - over the document type's
/// duplicated fields and JSON paths. A studio with no such model registers none, and a scan is described by its node and
/// table alone. Pure: handed to an evaluator in <see cref="HealthEvaluation.PlanAdvisor" />.
/// </remarks>
internal interface IHealthPlanAdvisor
{
    /// <summary>
    /// What a sequential scan of <paramref name="table" /> filters on and the index verdicts that need an index, or
    /// <see langword="null" /> when the studio has nothing to say about this table.
    /// </summary>
    /// <param name="table">One of the statement's own tables, read whole.</param>
    /// <param name="filter">The plan's deparsed <c>Filter</c>, or <see langword="null" /> for none.</param>
    /// <param name="indexesOnTable">The indexes on <paramref name="table" />, as the sweep read them.</param>
    HealthScanAdvice? AdviseSequentialScan(StudioTable table, string? filter, IReadOnlyList<IndexFacts> indexesOnTable);
}

/// <summary>What a sequential scan filters on, and what would serve it.</summary>
/// <param name="Subjects">Each member it filters on, as a person reads it.</param>
/// <param name="UnnamedMember">Whether it filters on a member whose key the recorded text does not name.</param>
/// <param name="Verdicts">The verdicts that need an index - each with the line that adds it.</param>
internal sealed record HealthScanAdvice(IReadOnlyList<string> Subjects, bool UnnamedMember, IReadOnlyList<HealthIndexVerdict> Verdicts);

/// <summary>One verdict that needs an index.</summary>
/// <param name="Subject">What the scan filters on.</param>
/// <param name="Reason">Why no index serves it, in words.</param>
/// <param name="Suggestion">The line that adds one.</param>
internal sealed record HealthIndexVerdict(string Subject, string Reason, string Suggestion);

/// <summary>
/// What a studio's own machinery adds to T14's reading of who holds vacuum back: whether a writer here also pins a
/// projection daemon's progress, and whether the source's own leader holds its lock in a transaction.
/// </summary>
/// <remarks>
/// MartenStudio answers from the store's configuration - its async shards and <c>Events.UseAdvisoryLockTransaction</c> -
/// read with no I/O. A studio with no daemon registers none, and T14 reads <see cref="HorizonSourceFacts.None" />.
/// </remarks>
internal interface IHorizonContributor
{
    /// <summary>The facts for the source <paramref name="context" /> sweeps. Configuration only: no connection, no daemon lookup.</summary>
    /// <param name="context">The sweep's context.</param>
    HorizonSourceFacts Describe(HealthCheckContext context);
}

/// <summary>What a source's own machinery means for T14.</summary>
/// <param name="HasAsyncShards">Whether it runs async projections, for which an open writer can pin the high-water mark (D01).</param>
/// <param name="LeaderLockInTransaction">Whether its daemon leader holds its leadership lock in a transaction it keeps open.</param>
internal sealed record HorizonSourceFacts(bool HasAsyncShards, bool LeaderLockInTransaction)
{
    /// <summary>A source with no daemon of its own.</summary>
    public static HorizonSourceFacts None { get; } = new(false, false);
}
