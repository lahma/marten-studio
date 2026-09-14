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
