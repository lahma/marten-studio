using MartenStudio.Internal.Sql;

namespace MartenStudio.Services.Query;

/// <summary>Which of the Query page's two modes a request is for.</summary>
internal enum QueryMode
{
    /// <summary>
    /// A Marten <c>where</c> clause against one registered document type. A read, needing no capability.
    /// </summary>
    Marten,

    /// <summary>
    /// The read-only SQL console. Gated on <see cref="MartenStudioCapabilities.RunSql" />.
    /// </summary>
    Sql,
}

/// <summary>
/// One registered document type, as the Query page's type picker shows it.
/// </summary>
/// <param name="Alias">Marten's document alias, which is what travels in the URL.</param>
/// <param name="TypeName">The short CLR type name.</param>
/// <param name="FullTypeName">The namespace-qualified CLR type name, for the picker's title.</param>
/// <param name="Schema">The schema the table lives in.</param>
/// <param name="Table">The table name, normally <c>mt_doc_&lt;alias&gt;</c>.</param>
/// <param name="QualifiedTableName">The quoted, schema-qualified table name, as the examples show it.</param>
/// <param name="DuplicatedColumns">The duplicated fields, which are the properties a filter can index.</param>
/// <param name="Conjoined">
/// Whether the collection is tenanted. The page says so, because a clause against a conjoined collection
/// read with no tenant selected is a cross-tenant read and the grid must not imply otherwise.
/// </param>
/// <param name="SoftDeleted">
/// Whether the collection is soft-deleted, which is what puts the deleted tri-state on the toolbar.
/// </param>
internal sealed record QueryDocumentTypeInfo(
    string Alias,
    string TypeName,
    string FullTypeName,
    string Schema,
    string Table,
    string QualifiedTableName,
    IReadOnlyList<string> DuplicatedColumns,
    bool Conjoined = false,
    bool SoftDeleted = false);

/// <summary>What the Marten mode was asked to run.</summary>
/// <param name="Alias">The document alias picked in the type list.</param>
/// <param name="WhereClause">
/// The clause as typed. <c>where …</c>, a bare predicate, and a trailing <c>order by …</c>,
/// <c>limit n</c> or <c>offset n</c> are all accepted; an empty clause matches everything. The predicate
/// is parenthesised <em>after</em> the studio's own tenant and soft-delete predicates, and the tail is
/// lifted out of it - see <see cref="QuerySqlComposer.TryPartition" />.
/// </param>
/// <param name="PageSize">
/// How many rows to ask for. Defaults to <see cref="MartenStudioOptions.DefaultPageSize" /> and is
/// clamped to <see cref="MartenStudioOptions.MaxPageSize" /> in the service - a page size that arrived
/// from a browser is a number somebody can type (plan §4.8).
/// </param>
/// <param name="IncludeDeleted">
/// What to do about soft-deleted rows, the same tri-state the documents browser offers. Ignored on a
/// collection that is not soft-deleted, which has no <c>mt_deleted</c> column to ask about.
/// </param>
internal sealed record MartenQueryRequest(
    string Alias,
    string? WhereClause,
    int? PageSize = null,
    DeletedFilter IncludeDeleted = DeletedFilter.Exclude);

/// <summary>One document the Marten mode returned.</summary>
/// <param name="Id">The document's id, as text, so it can go into a link.</param>
/// <param name="Json">
/// The <c>data</c> column exactly as Postgres holds it, read as <c>data::text</c>. Nothing deserializes
/// it: the studio never had the CLR type in its hands on this path, so no property can be dropped on the
/// way to the screen and no serializer of ours can disagree with the one Marten wrote it with
/// (AGENTS.md hard rule 10).
/// </param>
/// <param name="TenantId">
/// The row's tenant, on a conjoined collection. <see langword="null" /> when the collection is not
/// tenanted - and the one thing that makes a cross-tenant read legible rather than merely wrong.
/// </param>
/// <param name="IsDeleted">
/// Whether the row is soft-deleted, on a soft-deleted collection; <see langword="null" /> otherwise.
/// </param>
internal sealed record MartenQueryRow(string Id, string Json, string? TenantId = null, bool? IsDeleted = null);

/// <summary>
/// What the studio's own predicates did to a Mode A run - the half of the result that is about who may
/// see what rather than about what was asked.
/// </summary>
/// <remarks>
/// This exists because Marten's string-query path composes neither predicate. Mode A used to run through
/// <c>session.QueryAsync(type, clause)</c>, which returns every tenant's rows and every soft-deleted row
/// whatever the session's tenant is; the studio now composes and runs its own statement, and this record
/// is what the page shows so that a grid can never quietly be broader than it looks.
/// </remarks>
/// <param name="TenantId">The tenant the rows were filtered to, or <see langword="null" />.</param>
/// <param name="CrossTenant">
/// Whether this was a tenanted collection read across every tenant, because no tenant was in scope. The
/// results header says so out loud.
/// </param>
/// <param name="Deleted">What was done about soft-deleted rows.</param>
/// <param name="SoftDeleted">Whether the collection is soft-deleted at all.</param>
internal sealed record MartenQueryScope(
    string? TenantId,
    bool CrossTenant,
    DeletedFilter Deleted,
    bool SoftDeleted)
{
    /// <summary>A single-tenant collection with no soft delete: nothing to say.</summary>
    public static MartenQueryScope Plain { get; } = new(null, false, DeletedFilter.Exclude, false);
}

/// <summary>
/// What the Marten mode came back with: rows, the SQL it composed, and - when the console capability is
/// granted - the plan Postgres would use.
/// </summary>
/// <param name="Alias">The alias the request named, echoed so a stale result cannot be mislabelled.</param>
/// <param name="Rows">The documents, as their <c>data</c> column holds them.</param>
/// <param name="GeneratedSql">
/// The statement the studio composed and ran, character for character. It is the studio's own statement
/// rather than Marten's - see <see cref="QuerySqlComposer" /> for why - which is what makes this text, the
/// command that executed and the statement <c>EXPLAIN</c> planned all the same thing.
/// </param>
/// <param name="Parameters">
/// The values bound to it, described. The tenant, the row cap and the offset are parameters (AGENTS.md
/// hard rule 4); the predicate is the visitor's own SQL and is not.
/// </param>
/// <param name="Duration">How long the query itself took.</param>
/// <param name="RowLimit">The row cap that was in force.</param>
/// <param name="LimitApplied">
/// Whether the cap is the studio's page size. It is not when the clause carried a <c>limit</c> of its own
/// - which is clamped down to the page size and never up.
/// </param>
/// <param name="Error">The Postgres error, when the clause did not run.</param>
/// <param name="Plan">The plan, when one was asked for and could be fetched.</param>
/// <param name="Scope">
/// What the studio's own tenant and soft-delete predicates did, so the page can say it.
/// </param>
/// <param name="Predicate">
/// The visitor's predicate as it appears inside the parentheses, which is what turns a Postgres error
/// position in the composed statement back into a caret under the character they typed.
/// </param>
/// <param name="Rejection">
/// Why the clause was never sent. A <c>where</c> clause runs without a capability, so it is held to being
/// a fragment of one statement that reads only the table it filters, with a tail the composer can place -
/// see <see cref="QuerySqlComposer.CheckClause" />.
/// </param>
internal sealed record MartenQueryResult(
    string Alias,
    IReadOnlyList<MartenQueryRow> Rows,
    string GeneratedSql,
    IReadOnlyList<string> Parameters,
    TimeSpan Duration,
    int RowLimit,
    bool LimitApplied,
    SqlError? Error,
    ExplainResult? Plan,
    MartenQueryScope? Scope = null,
    string Predicate = "",
    SqlRejection? Rejection = null)
{
    /// <summary>Whether the clause ran without a Postgres error.</summary>
    public bool Succeeded => Error is null && Rejection is null;

    /// <summary>Whether the result was cut off by the row cap.</summary>
    /// <remarks>
    /// A cap of zero - which a clause of <c>limit 0</c> asks for - is not a truncation: nothing was cut
    /// off, nothing was asked for.
    /// </remarks>
    public bool Truncated => RowLimit > 0 && Rows.Count >= RowLimit;

    /// <summary>The scope facts, with the do-nothing default for a plain collection.</summary>
    public MartenQueryScope ScopeOrPlain => Scope ?? MartenQueryScope.Plain;
}

/// <summary>
/// A query plan, or the reason there is not one.
/// </summary>
/// <remarks>
/// "Cannot report" is a value here, not an exception (plan §4.8): the plan needs the SQL console
/// capability, because fetching one means sending a statement the studio composed to Postgres, and a
/// studio without <c>RunSql</c> must not do that. The page says so where the plan would have been.
/// </remarks>
/// <param name="PlanText">The plan as Postgres' own text output, when it is available.</param>
/// <param name="PlanJson">The plan as <c>EXPLAIN (FORMAT JSON)</c> returned it.</param>
/// <param name="Error">What Postgres said, when the explain itself failed.</param>
/// <param name="UnavailableReason">Why there is no plan at all.</param>
internal sealed record ExplainResult(
    string? PlanText,
    string? PlanJson,
    SqlError? Error,
    string? UnavailableReason)
{
    /// <summary>Whether there is a plan to draw.</summary>
    public bool HasPlan => !string.IsNullOrWhiteSpace(PlanText) || !string.IsNullOrWhiteSpace(PlanJson);

    /// <summary>A plan that was never asked for, with the reason it was not.</summary>
    public static ExplainResult Unavailable(string reason) => new(null, null, null, reason);
}

/// <summary>What the SQL console was asked to run.</summary>
/// <param name="Statement">The statement as typed. Never rewritten before it is sent.</param>
internal sealed record SqlConsoleRequest(string Statement);

/// <summary>
/// What the SQL console came back with, including the two ways it can refuse before Postgres sees
/// anything.
/// </summary>
/// <param name="Columns">The columns, with the Postgres type of each for the grid header.</param>
/// <param name="Rows">The rows, already formatted and capped by <see cref="SqlValueFormatter" />.</param>
/// <param name="Truncated">Whether the reader was stopped at the row cap.</param>
/// <param name="RowLimit">The row cap that was in force.</param>
/// <param name="Duration">How long the statement took.</param>
/// <param name="Notices">Anything Postgres raised as a notice, for the Messages tab.</param>
/// <param name="Error">The Postgres error, when there was one.</param>
/// <param name="Rejection">The guard's refusal, when the statement was never sent.</param>
internal sealed record SqlConsoleResult(
    IReadOnlyList<SqlResultColumn> Columns,
    IReadOnlyList<IReadOnlyList<SqlCell>> Rows,
    bool Truncated,
    int RowLimit,
    TimeSpan Duration,
    IReadOnlyList<string> Notices,
    SqlError? Error,
    SqlRejection? Rejection)
{
    /// <summary>Whether the statement ran and Postgres was happy with it.</summary>
    public bool Succeeded => Error is null && Rejection is null;

    /// <summary>The result of a statement the guard refused.</summary>
    public static SqlConsoleResult Refused(SqlRejection rejection, int rowLimit) =>
        new([], [], false, rowLimit, TimeSpan.Zero, [], null, rejection);
}

/// <summary>
/// The statement guard's refusal, as a value the editor can point at.
/// </summary>
/// <remarks>
/// <see cref="Position" /> is a zero-based character offset into the statement - the same convention
/// <see cref="SqlGuardResult" /> uses - so the editor can show the offending character with a caret under
/// it rather than only saying that something was wrong.
/// </remarks>
/// <param name="Reason">Which rule refused it, for the tests and the log.</param>
/// <param name="Message">What to tell the person who typed it.</param>
/// <param name="Token">The offending token, when there is one.</param>
/// <param name="Position">Where in the statement the problem is, zero-based.</param>
internal sealed record SqlRejection(string Reason, string Message, string? Token, int Position)
{
    /// <summary>Adapts the guard's own answer. Unknown reasons keep working: the name is carried as text.</summary>
    public static SqlRejection FromGuard(SqlGuardResult result) =>
        new(result.Reason.ToString(), result.Message, result.Token, result.Position);
}

/// <summary>One example the idle panel offers, built from this store's own aliases and tables.</summary>
/// <param name="Mode">Which editor it belongs in.</param>
/// <param name="Title">What it demonstrates.</param>
/// <param name="Snippet">The text that goes into the editor.</param>
/// <param name="Alias">The document alias it is about, so picking it also picks the type.</param>
internal sealed record QueryExample(QueryMode Mode, string Title, string Snippet, string? Alias);

/// <summary>The examples for both modes.</summary>
/// <param name="Where">Three <c>where</c> clauses.</param>
/// <param name="Sql">Three SQL statements.</param>
internal sealed record QueryExamples(IReadOnlyList<QueryExample> Where, IReadOnlyList<QueryExample> Sql)
{
    /// <summary>No store, no examples - which is what a studio with no document types shows.</summary>
    public static QueryExamples None { get; } = new([], []);
}
