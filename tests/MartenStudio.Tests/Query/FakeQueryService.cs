using MartenStudio.Internal.Sql;
using MartenStudio.Services;
using MartenStudio.Services.Query;

namespace MartenStudio.Tests.Query;

/// <summary>
/// What the Query page is given, said outright by a test.
/// </summary>
/// <remarks>
/// Hand-written rather than a mocking framework (AGENTS.md package budget): the data seam is our own set
/// of interfaces, and a fake that records what it was asked is smaller and far more readable than a
/// call-matching DSL. It can also throw the two refusals the page has to render - a capability denial and
/// a scope denial - which is the whole point of having a seam there.
/// </remarks>
internal sealed class FakeQueryService : IQueryService
{
    /// <summary>The document types the picker is offered.</summary>
    public List<QueryDocumentTypeInfo> DocumentTypes { get; } = [];

    /// <summary>The examples the idle panel shows.</summary>
    public QueryExamples Examples { get; set; } = QueryExamples.None;

    /// <summary>What the Marten mode answers.</summary>
    public MartenQueryResult? MartenResult { get; set; }

    /// <summary>What the SQL console answers.</summary>
    public SqlConsoleResult? SqlResult { get; set; }

    /// <summary>What the Marten mode throws instead of answering.</summary>
    public Exception? MartenFailure { get; set; }

    /// <summary>
    /// When set, the fake runs the <em>real</em> clause guard over whatever the page handed it and answers
    /// its refusal, instead of returning <see cref="MartenResult" />.
    /// </summary>
    /// <remarks>
    /// A fake that always says yes cannot prove anything about text that has to be refused. This is not a
    /// second implementation of the service - it is one call to
    /// <see cref="QuerySqlComposer.CheckClause" />, the same call <c>QueryService</c> makes - and it exists
    /// so a page test can assert end to end that a clause arriving through a URL reaches the guard byte for
    /// byte. A carriage return that the router, the parameter binding or the editor turned into a line feed
    /// somewhere in between would be a clause the guard reads differently from the one Postgres runs, and
    /// no test that stopped at "the page sent something" would notice.
    /// </remarks>
    public bool UseRealClauseGuard { get; set; }

    /// <summary>What the real guard is told about <c>RunSql</c>, when it is running at all.</summary>
    public bool AllowNestedReads { get; set; } = true;

    /// <summary>What the SQL console throws instead of answering.</summary>
    public Exception? SqlFailure { get; set; }

    /// <summary>Every Marten request the page made.</summary>
    public List<MartenQueryRequest> MartenRequests { get; } = [];

    /// <summary>Every statement the page sent to the console.</summary>
    public List<string> Statements { get; } = [];

    /// <summary>Set to block the next run until <see cref="Release" /> is called, so "running" is visible.</summary>
    public TaskCompletionSource? Gate { get; set; }

    /// <summary>Whether the last run saw its token cancelled.</summary>
    public bool Cancelled { get; private set; }

    public FakeQueryService WithType(
        string alias,
        string typeName = "Person",
        bool conjoined = false,
        bool softDeleted = false)
    {
        DocumentTypes.Add(new QueryDocumentTypeInfo(
            alias,
            typeName,
            "MartenStudio.Sample." + typeName,
            "studio",
            "mt_doc_" + alias,
            "\"studio\".\"mt_doc_" + alias + "\"",
            ["Name"],
            conjoined,
            softDeleted));

        return this;
    }

    public Task<IReadOnlyList<QueryDocumentTypeInfo>> ListDocumentTypesAsync(
        StudioScope scope,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<QueryDocumentTypeInfo>>(DocumentTypes);

    public Task<QueryExamples> BuildExamplesAsync(StudioScope scope, CancellationToken cancellationToken = default) =>
        Task.FromResult(Examples);

    public async Task<MartenQueryResult> RunMartenQueryAsync(
        StudioScope scope,
        MartenQueryRequest request,
        CancellationToken cancellationToken = default)
    {
        MartenRequests.Add(request);

        await WaitAsync(cancellationToken);

        if (MartenFailure is not null)
        {
            throw MartenFailure;
        }

        if (UseRealClauseGuard)
        {
            SqlGuardResult guard = QuerySqlComposer.CheckClause(request.WhereClause, AllowNestedReads);

            if (!guard.Allowed)
            {
                return new MartenQueryResult(
                    request.Alias, [], string.Empty, [], TimeSpan.Zero, 50, true, null,
                    ExplainResult.Unavailable("The clause was never sent."), null, string.Empty,
                    SqlRejection.FromGuard(guard));
            }
        }

        return MartenResult ?? new MartenQueryResult(
            request.Alias, [],
            "select d.\"id\", d.\"data\"::text\nfrom \"studio\".\"mt_doc_person\" as d\nwhere 1 = 1\nlimit @limit",
            ["@limit = 50"], TimeSpan.FromMilliseconds(3), 50, true, null, null);
    }

    public async Task<SqlConsoleResult> RunSqlAsync(
        StudioScope scope,
        SqlConsoleRequest request,
        CancellationToken cancellationToken = default)
    {
        Statements.Add(request.Statement);

        await WaitAsync(cancellationToken);

        if (SqlFailure is not null)
        {
            throw SqlFailure;
        }

        return SqlResult ?? new SqlConsoleResult(
            [], [], false, 500, TimeSpan.FromMilliseconds(2), [], null, null);
    }

    /// <summary>Lets a gated run finish.</summary>
    public void Release() => Gate?.TrySetResult();

    private async Task WaitAsync(CancellationToken cancellationToken)
    {
        if (Gate is null)
        {
            return;
        }

        try
        {
            await Gate.Task.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            Cancelled = true;
            throw;
        }
    }

    /// <summary>A result with one row of two columns, one of them JSON and one of them NULL.</summary>
    public static SqlConsoleResult OneRow() =>
        new(
            [new SqlResultColumn("id", "uuid"), new SqlResultColumn("doc", "jsonb"), new SqlResultColumn("note", "text")],
            [
                [
                    new SqlCell("6f1b8b1e-0000-0000-0000-000000000001", SqlCellKind.Uuid, false, 36),
                    new SqlCell("""{"name":"Alice"}""", SqlCellKind.Json, false, 16),
                    SqlCell.Null,
                ],
            ],
            Truncated: false,
            RowLimit: 500,
            Duration: TimeSpan.FromMilliseconds(7),
            Notices: ["NOTICE: something happened"],
            Error: null,
            Rejection: null);
}
