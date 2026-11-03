using JasperFx.Events;

using PostgresStudio.Services.Query;

namespace MartenStudio.Services.Query;

/// <summary>What a LINQ query reads: a document type, or the event log through Marten's <c>QueryRawEvents()</c> (1.1).</summary>
internal enum LinqQueryTarget
{
    /// <summary>One document type, picked by alias.</summary>
    Documents,

    /// <summary>The event log (<c>mt_events</c>). The request's alias is not read: the target decides.</summary>
    Events,
}

/// <summary>What to do about archived events, through Marten's own filters (1.1).</summary>
internal enum ArchivedFilter
{
    /// <summary>Leave them out: Marten's own <c>is_archived = FALSE</c> stays, as it does for the host's <c>QueryRawEvents()</c>.</summary>
    Exclude,

    /// <summary>Live and archived, through Marten's <c>MaybeArchived()</c>.</summary>
    Include,

    /// <summary>Archived only, through <c>IsArchived</c> composed by the studio.</summary>
    Only,
}

/// <summary>Which half of a LINQ query a refusal is about.</summary>
internal enum LinqClause
{
    /// <summary>Neither: the refusal is about the request as a whole (the type, the page, the mode).</summary>
    None,

    /// <summary>The predicate.</summary>
    Where,

    /// <summary>The ordering.</summary>
    OrderBy,
}

/// <summary>
/// How far a LINQ query got before it was refused - the stages of the pipeline, in order.
/// </summary>
/// <remarks>
/// <para>
/// The order is the threat model. Everything up to and including <see cref="Guard" /> happens before Marten
/// sees the expression, and Marten is the component that evaluates in this process any part of an expression
/// that does not reference the document (<c>LinqInternalExtensions.ReduceToConstant</c>, verified at Marten
/// 9.39.0 and 9.50.2). A hostile input must therefore stop at <see cref="Screen" />, <see cref="Parse" /> or
/// <see cref="Guard" />, and the hostile-input theory asserts exactly that.
/// </para>
/// </remarks>
internal enum LinqStage
{
    /// <summary>The mode is not available in this process: an older Marten, Native AOT, an unknown shape.</summary>
    Unavailable,

    /// <summary>The studio's own text screen, before the text reaches any parser.</summary>
    Screen,

    /// <summary>JasperFx's Dynamic LINQ parse and its allow-list (with the studio's guard as its first rule).</summary>
    Parse,

    /// <summary>The studio's independent walk of the composed expression, after the parse and before Marten.</summary>
    Guard,

    /// <summary>Marten's own LINQ translation: a shape it cannot turn into SQL.</summary>
    Translate,

    /// <summary>Postgres, or the connection to it.</summary>
    Database,
}

/// <summary>Why a LINQ query was not run, or did not finish, and where to point.</summary>
/// <param name="Stage">How far it got.</param>
/// <param name="Clause">Which input box it is about.</param>
/// <param name="Message">What to tell the person who typed it.</param>
/// <param name="Position">A zero-based offset into that box's text, or -1 when there is nothing to point at.</param>
internal sealed record LinqRefusal(LinqStage Stage, LinqClause Clause, string Message, int Position = -1);

/// <summary>What LINQ mode was asked to run.</summary>
/// <param name="Alias">The document alias picked in the type list - a root type or one of its subclasses.</param>
/// <param name="Where">
/// The predicate, in JasperFx's Dynamic LINQ dialect (<c>Status == "Open" and Total &gt; 100</c>). Blank
/// matches everything the session can see.
/// </param>
/// <param name="OrderBy">The ordering (<c>Total desc, Name</c>), or blank for the id order.</param>
/// <param name="PageSize">
/// How many documents to show. Defaults to <see cref="MartenStudioOptions.DefaultPageSize" /> and is clamped to
/// <see cref="MartenStudioOptions.MaxPageSize" /> in the service.
/// </param>
/// <param name="Offset">
/// Where the page starts. LINQ mode pages with Marten's own <c>Skip</c> / <c>Take</c>, which is
/// <c>OFFSET</c> paging, and stops at <see cref="LinqQueryLimits.MaxOffset" /> - see
/// <see cref="LinqQueryService" /> for why it is not keyset paging.
/// </param>
/// <param name="Deleted">
/// What to do about soft-deleted documents, through Marten's own <c>MaybeDeleted()</c> / <c>IsDeleted()</c>.
/// Ignored on a collection that is not soft-deleted.
/// </param>
/// <param name="IncludePlan">Whether to fetch the plan with <c>EXPLAIN</c> (never <c>ANALYZE</c>).</param>
/// <param name="Target">
/// What is queried (1.1). The service decides on this field, never on an alias string: for
/// <see cref="LinqQueryTarget.Events" /> the alias is replaced by <see cref="LinqQueryService.EventsAlias" />.
/// </param>
/// <param name="Archived">What to do about archived events; read only when <paramref name="Target" /> is the events.</param>
internal sealed record LinqQueryRequest(
    string Alias,
    string? Where,
    string? OrderBy = null,
    int? PageSize = null,
    int Offset = 0,
    DeletedFilter Deleted = DeletedFilter.Exclude,
    bool IncludePlan = true,
    LinqQueryTarget Target = LinqQueryTarget.Documents,
    ArchivedFilter Archived = ArchivedFilter.Exclude);

/// <summary>One document LINQ mode returned.</summary>
/// <param name="Id">The document's id as text, so it can go into a link; empty when Marten did not select it.</param>
/// <param name="Json">
/// The <c>data</c> column exactly as Postgres holds it, read as text and never deserialized (AGENTS.md hard
/// rule 10 the easy way: nothing serializes). <see langword="null" /> when it was not inlined - see
/// <see cref="NotInlined" />.
/// </param>
/// <param name="TenantId">The row's tenant, when Marten selected the column.</param>
/// <param name="IsDeleted">Whether the row is soft-deleted, when Marten selected the column.</param>
/// <param name="NotInlined">Why <see cref="Json" /> is missing: too large to inline, or past the page's budget.</param>
internal sealed record LinqQueryRow(
    string Id,
    string? Json,
    string? TenantId = null,
    bool? IsDeleted = null,
    string? NotInlined = null);

/// <summary>One event LINQ mode returned (1.1). The binary payload and the headers are never read.</summary>
/// <param name="Sequence">The global sequence. Not unique under per-tenant sequences: rows are keyed by position.</param>
/// <param name="Id">The event's own id.</param>
/// <param name="StreamId">The stream's id or key as text, either identity.</param>
/// <param name="Version">The version within its stream.</param>
/// <param name="EventTypeName">The event type name.</param>
/// <param name="DotNetTypeName">The .NET type name the store recorded.</param>
/// <param name="Timestamp">When it was appended.</param>
/// <param name="TenantId">The row's tenant.</param>
/// <param name="IsArchived">Whether its stream is archived.</param>
/// <param name="CorrelationId">The correlation id, when the store records it.</param>
/// <param name="CausationId">The causation id, when the store records it.</param>
/// <param name="UserName">The user name, when the store records it.</param>
/// <param name="HasBinary">Whether the row carries a binary payload (only its nullness is read).</param>
/// <param name="Json">The body as Postgres holds it; <see langword="null" /> when it was not inlined.</param>
/// <param name="NotInlined">Why <paramref name="Json" /> is missing.</param>
internal sealed record LinqEventRow(
    long Sequence,
    string Id,
    string StreamId,
    long Version,
    string EventTypeName,
    string? DotNetTypeName,
    DateTimeOffset? Timestamp,
    string? TenantId,
    bool IsArchived,
    string? CorrelationId,
    string? CausationId,
    string? UserName,
    bool HasBinary,
    string? Json,
    string? NotInlined);

/// <summary>What the tenant and archive terms of an event query meant, so the page can say it (1.1).</summary>
/// <param name="TenantId">The tenant Marten narrowed the query to, when it did.</param>
/// <param name="CrossTenant">Whether Marten was told to read every tenant (<c>AnyTenant()</c>).</param>
/// <param name="Archived">What was done about archived events.</param>
/// <param name="WholeDatabase">Whether the events are not divided by tenant, so the query read the whole database's log.</param>
/// <param name="PerTenantSequences">Whether sequences are counted per tenant, so two rows can share one.</param>
internal sealed record LinqEventScope(
    string? TenantId,
    bool CrossTenant,
    ArchivedFilter Archived,
    bool WholeDatabase,
    bool PerTenantSequences);

/// <summary>An event member LINQ mode does not offer, and the sentence that says why.</summary>
/// <param name="Name">The member, or the members the sentence covers together.</param>
/// <param name="Reason">One sentence.</param>
internal sealed record LinqWithheldMember(string Name, string Reason);

/// <summary>What the Query page needs to draw the events target (1.1).</summary>
/// <param name="Conjoined">Whether the store's events are conjoined-tenanted.</param>
/// <param name="PerTenantSequences">Whether sequences are counted per tenant.</param>
/// <param name="StreamIdentity">Whether streams are identified by Guid or by string.</param>
/// <param name="Members">The members a predicate may name, in the hint's order.</param>
/// <param name="Withheld">Each member a visitor might expect that is not offered here, with why.</param>
/// <param name="DefaultOrdering">The ordering used when the box is empty, as the box's placeholder spells it.</param>
/// <param name="EventsSchema">The schema the event tables live in.</param>
internal sealed record LinqEventTargetInfo(
    bool Conjoined,
    bool PerTenantSequences,
    StreamIdentity StreamIdentity,
    IReadOnlyList<string> Members,
    IReadOnlyList<LinqWithheldMember> Withheld,
    string DefaultOrdering,
    string EventsSchema);

/// <summary>One parameter of the statement Marten built, described without its value (D14).</summary>
/// <param name="Name">The parameter's name as the statement spells it (<c>:p0</c>).</param>
/// <param name="Type">Its Npgsql type, or <c>unknown</c>.</param>
internal sealed record LinqParameterInfo(string Name, string Type);

/// <summary>
/// What Postgres said about a LINQ statement that ran and failed, with every withheld schema masked (D27).
/// </summary>
/// <param name="SqlState">The five-character SQLSTATE.</param>
/// <param name="Message">Postgres' message, masked; or a sentence of the studio's own when it may not be shown.</param>
/// <param name="Detail">Postgres' detail line, masked, when there is one.</param>
internal sealed record LinqQueryError(string SqlState, string Message, string? Detail = null);

/// <summary>One index verdict LINQ mode derived from the predicate and the ordering.</summary>
/// <param name="Subject">What it is about: the member path (<c>Customer.Name</c>), <c>id</c>, or the ordering.</param>
/// <param name="Verdict">The index advisor's verdict for it, with the <c>StoreOptions</c> line when there is one.</param>
internal sealed record LinqIndexVerdict(string Subject, IndexVerdict Verdict);

/// <summary>One scan in the plan Postgres chose, as <c>EXPLAIN (FORMAT JSON)</c> reported it.</summary>
/// <param name="NodeType">The plan node: <c>Seq Scan</c>, <c>Index Scan</c>, <c>Bitmap Heap Scan</c>, ...</param>
/// <param name="Relation">The table it reads, when the node names one.</param>
/// <param name="Index">The index it uses, when it uses one.</param>
internal sealed record LinqPlanScan(string NodeType, string? Relation, string? Index);

/// <summary>
/// The index advice for a LINQ query: the static verdicts, and what the plan actually did.
/// </summary>
/// <param name="Worst">The worst verdict, which is what the panel's header shows.</param>
/// <param name="Verdicts">One per filtered member and one for the ordering, in the order they appear.</param>
/// <param name="Scans">The scans in the plan, when a plan was fetched; empty otherwise.</param>
/// <param name="Note">A sentence that joins the two up, when the plan and the verdicts disagree.</param>
internal sealed record LinqIndexAdvice(
    IndexVerdictLevel Worst,
    IReadOnlyList<LinqIndexVerdict> Verdicts,
    IReadOnlyList<LinqPlanScan> Scans,
    string? Note = null)
{
    /// <summary>
    /// X2: the index Marten would create for each verdict whose line declares one on a member - Marten's own DDL, for hypopg to
    /// imagine - in the order of the verdicts. Empty when no verdict suggests one hypopg can imagine.
    /// </summary>
    public IReadOnlyList<HypotheticalIndex> WhatIf { get; init; } = [];

    /// <summary>No predicate, no ordering, no plan: nothing to advise.</summary>
    public static LinqIndexAdvice None { get; } = new(IndexVerdictLevel.Green, [], []);
}

/// <summary>What one LINQ run came back with.</summary>
/// <param name="Alias">The alias the request named, echoed so a stale result cannot be mislabelled.</param>
/// <param name="Rows">The documents, at most one page of them.</param>
/// <param name="HasMore">Whether a further page exists (one more row than the page was read).</param>
/// <param name="Offset">Where this page starts.</param>
/// <param name="PageSize">The page size that was in force, after clamping.</param>
/// <param name="Sql">
/// The statement Marten built, character for character - what ran and what the plan is for. Empty when the
/// query never got as far as Marten.
/// </param>
/// <param name="Parameters">Its parameters, names and types only (D14).</param>
/// <param name="Duration">How long the statement took, measured around its execution alone.</param>
/// <param name="Refusal">Why it was not sent, or stopped at Marten's translation.</param>
/// <param name="Error">What Postgres said when it ran and failed.</param>
/// <param name="Plan">The plan, or why there is none.</param>
/// <param name="Advice">The index advice, when the query was composed.</param>
/// <param name="Scope">What the tenant and soft-delete terms Marten applied mean, so the page can say it.</param>
/// <param name="BudgetSpent">Whether the page's text budget was spent before every row was inlined.</param>
internal sealed record LinqQueryResult(
    string Alias,
    IReadOnlyList<LinqQueryRow> Rows,
    bool HasMore,
    int Offset,
    int PageSize,
    string Sql,
    IReadOnlyList<LinqParameterInfo> Parameters,
    TimeSpan Duration,
    LinqRefusal? Refusal = null,
    LinqQueryError? Error = null,
    ExplainResult? Plan = null,
    LinqIndexAdvice? Advice = null,
    MartenQueryScope? Scope = null,
    bool BudgetSpent = false)
{
    /// <summary>X2: what a what-if came to, for a run made to compare plans rather than to read rows.</summary>
    public WhatIfResult? WhatIf { get; init; }

    /// <summary>1.1: the events, for a run over the event log; <see cref="Rows" /> is empty then.</summary>
    public IReadOnlyList<LinqEventRow> EventRows { get; init; } = [];

    /// <summary>1.1: what the tenant and archive terms of an event run meant; <see langword="null" /> for documents.</summary>
    public LinqEventScope? EventScope { get; init; }

    /// <summary>How many rows came back, documents or events.</summary>
    public int RowCount => Rows.Count + EventRows.Count;

    /// <summary>Whether the query ran and Postgres was happy with it.</summary>
    public bool Succeeded => Refusal is null && Error is null;

    /// <summary>A query that was refused before anything ran.</summary>
    public static LinqQueryResult Refused(string alias, LinqRefusal refusal, int offset, int pageSize, string sql = "",
        IReadOnlyList<LinqParameterInfo>? parameters = null) =>
        new(alias, [], false, offset, pageSize, sql, parameters ?? [], TimeSpan.Zero, refusal);
}

/// <summary>Whether LINQ mode can be offered in this process, and why not.</summary>
internal enum LinqAvailability
{
    /// <summary>It can.</summary>
    Available,

    /// <summary><see cref="MartenStudioCapabilities.RunLinqQueries" /> is off.</summary>
    CapabilityOff,

    /// <summary><see cref="MartenStudioOptions.ReadOnly" /> is on.</summary>
    ReadOnly,

    /// <summary>
    /// The JasperFx, Marten or System.Linq.Dynamic.Core this process loaded is not the shape the studio was
    /// verified against, so the mode fails closed rather than guessing.
    /// </summary>
    Unrecognised,

    /// <summary>The process cannot generate code at run time (Native AOT), which Dynamic LINQ needs.</summary>
    NoDynamicCode,
}

/// <summary>One document type LINQ mode may query: a visible root type, or a visible subclass of one.</summary>
/// <param name="Alias">Marten's alias, which travels in the URL.</param>
/// <param name="TypeName">The short CLR type name.</param>
/// <param name="FullTypeName">The namespace-qualified CLR type name, for the picker's title.</param>
/// <param name="RootAlias">The root's alias for a subclass, or <see langword="null" /> for a root.</param>
/// <param name="Conjoined">Whether the collection is conjoined-tenanted.</param>
/// <param name="SoftDeleted">Whether the collection is soft-deleted, which puts the deleted selector on the toolbar.</param>
/// <param name="Members">A few of its queryable members, as the predicate box's hint lists them.</param>
internal sealed record LinqDocumentTypeInfo(
    string Alias,
    string TypeName,
    string FullTypeName,
    string? RootAlias,
    bool Conjoined,
    bool SoftDeleted,
    IReadOnlyList<string> Members);

/// <summary>What the Query page needs before it can draw LINQ mode.</summary>
/// <param name="Availability">Whether the mode is there at all.</param>
/// <param name="Message">The sentence that says why not - the option to set, or the Marten version it needs.</param>
/// <param name="Types">The document types the visitor may query; empty unless the mode is available.</param>
/// <param name="MayRun">
/// Whether the write policy, asked with <c>RunLinqQueries</c> for the scope on screen, lets this visitor run
/// one. Said, not enforced: <see cref="ILinqQueryService.RunAsync" /> refuses regardless.
/// </param>
/// <param name="MayExplain">
/// Whether the same visitor may also see the plan, which is asked of the database as a whole
/// (<c>TenantId = null</c>).
/// </param>
internal sealed record LinqModeStatus(
    LinqAvailability Availability,
    string? Message,
    IReadOnlyList<LinqDocumentTypeInfo> Types,
    bool MayRun = false,
    bool MayExplain = false)
{
    /// <summary>1.1: the events target, always offered when the mode is available; computed from options, never by connecting.</summary>
    public LinqEventTargetInfo? Events { get; init; }

    /// <summary>Whether the mode can be used at all.</summary>
    public bool IsAvailable => Availability == LinqAvailability.Available;
}

/// <summary>
/// The caps LINQ mode applies, in one place so that the screen, the guard, the policy handed to JasperFx and
/// the documentation cannot drift apart.
/// </summary>
/// <remarks>
/// <para>
/// Every one of these is tighter than JasperFx's own default (2,000 characters, 500 nodes, 64 arguments),
/// because a predicate typed into a box by hand needs none of that headroom and every unit of it is a unit an
/// adversary gets to spend. The parser is recursive descent, so <see cref="MaxDepth" /> and
/// <see cref="MaxUnaryRun" /> are what keep a pathological nesting from becoming a stack overflow - which a
/// .NET process cannot catch - before it ever reaches the parser.
/// </para>
/// </remarks>
internal static class LinqQueryLimits
{
    /// <summary>Longest predicate accepted, in characters.</summary>
    public const int MaxWhereLength = 1_000;

    /// <summary>Longest ordering accepted, in characters.</summary>
    public const int MaxOrderByLength = 200;

    /// <summary>Most tokens in one box.</summary>
    public const int MaxTokens = 200;

    /// <summary>Deepest parenthesis nesting in one box.</summary>
    public const int MaxDepth = 12;

    /// <summary>Most <c>!</c> / <c>not</c> in a row.</summary>
    public const int MaxUnaryRun = 3;

    /// <summary>Most expression nodes in one parsed clause - the cap handed to JasperFx and the one the guard applies.</summary>
    public const int MaxNodes = 200;

    /// <summary>Deepest the guard will recurse into a parsed expression.</summary>
    public const int MaxExpressionDepth = 160;

    /// <summary>Longest string literal, in characters.</summary>
    public const int MaxLiteralLength = 256;

    /// <summary>Most ordering keys.</summary>
    public const int MaxOrderKeys = 4;

    /// <summary>
    /// The furthest a page may start. LINQ mode pages with <c>OFFSET</c>, whose cost grows with the offset; past
    /// this the visitor is told to narrow the predicate instead.
    /// </summary>
    public const int MaxOffset = 10_000;

    /// <summary>
    /// How many characters of document JSON one page may inline before the rest of its rows are listed by id
    /// alone. Each document is also capped at <see cref="MartenStudioOptions.MaxInlineDocumentBytes" />.
    /// </summary>
    public const int PageTextBudget = 8 * 1024 * 1024;

    /// <summary>How deep into nested objects the queryable member graph reaches.</summary>
    public const int GraphMaxDepth = 4;

    /// <summary>Most members in one document type's queryable graph.</summary>
    public const int GraphMaxMembers = 512;

    /// <summary>Most types in one document type's queryable graph.</summary>
    public const int GraphMaxTypes = 64;

    /// <summary>
    /// The oldest System.Linq.Dynamic.Core the mode runs on: what JasperFx 2.84 was built and tested against,
    /// and past both CVE-2023-32571 (fixed in 1.3.0) and CVE-2024-51417 (fixed in 1.6.0).
    /// </summary>
    public static readonly Version MinimumDynamicLinq = new(1, 7, 4);
}
