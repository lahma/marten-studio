using MartenStudio.Services;
using MartenStudio.Services.Database;

namespace MartenStudio.Tests.Support;

/// <summary>
/// What the database browser's Rows tab and row detail are given, said outright by a test.
/// </summary>
/// <remarks>
/// A hand-written fake rather than a mocking framework (AGENTS.md package budget). Every answer is a
/// settable property, and a <see langword="null" /> one answers from <see cref="FakeTableRows" /> for
/// whatever was asked; every call is recorded, so a page test can assert which relation, request, key,
/// column or filter a page asked with. <see cref="Failure" /> makes every method throw, for the page's
/// error frame - the real service never throws but for cancellation.
/// </remarks>
internal sealed class FakeTableRowService : ITableRowService
{
    /// <summary>What <see cref="ListRowsAsync" /> answers; <see langword="null" /> answers a page of five Quartz rows.</summary>
    public TableRowPage? Page { get; set; }

    /// <summary>What <see cref="GetRowAsync" /> answers; <see langword="null" /> answers row zero.</summary>
    public TableRowDetail? Detail { get; set; }

    /// <summary>What <see cref="GetReferencesAsync" /> answers; <see langword="null" /> answers row zero's.</summary>
    public TableRowReferences? References { get; set; }

    /// <summary>What <see cref="CountExactAsync" /> answers; <see langword="null" /> answers 24, exactly.</summary>
    public TableRowCount? Count { get; set; }

    /// <summary>What <see cref="GetCellAsync" /> answers; <see langword="null" /> answers the asked column of row zero.</summary>
    public TableCellValue? Cell { get; set; }

    /// <summary>What every method throws, when a test is about the failure frame.</summary>
    public Exception? Failure { get; set; }

    /// <summary>How many calls of any kind have been made.</summary>
    public int Reads { get; private set; }

    /// <summary>The scope the last call was made with.</summary>
    public StudioScope? LastScope { get; private set; }

    /// <summary>The scope of every call, in order.</summary>
    public List<StudioScope> Scopes { get; } = [];

    /// <summary>Every page asked for, with the relation it was asked of.</summary>
    public List<(string Schema, string Name, TableRowRequest Request)> Requests { get; } = [];

    /// <summary>Every row key <see cref="GetRowAsync" /> was asked for.</summary>
    public List<IReadOnlyDictionary<string, string>> RowsAsked { get; } = [];

    /// <summary>Every row key <see cref="GetReferencesAsync" /> was asked for.</summary>
    public List<IReadOnlyDictionary<string, string>> ReferencesAsked { get; } = [];

    /// <summary>Every filter <see cref="CountExactAsync" /> was asked with.</summary>
    public List<string?> CountsAsked { get; } = [];

    /// <summary>Every cell <see cref="GetCellAsync" /> was asked for.</summary>
    public List<(IReadOnlyDictionary<string, string> Key, string Column)> CellsAsked { get; } = [];

    public Task<TableRowPage> ListRowsAsync(
        StudioScope scope,
        string schema,
        string name,
        TableRowRequest request,
        CancellationToken cancellationToken = default)
    {
        Record(scope);
        Requests.Add((schema, name, request));

        return Answer(Page ?? FakeTableRows.Page(filter: request.Filter));
    }

    public Task<TableRowDetail> GetRowAsync(
        StudioScope scope,
        string schema,
        string name,
        IReadOnlyDictionary<string, string> key,
        CancellationToken cancellationToken = default)
    {
        Record(scope);
        RowsAsked.Add(key);

        return Answer(Detail ?? FakeTableRows.Detail());
    }

    public Task<TableRowReferences> GetReferencesAsync(
        StudioScope scope,
        string schema,
        string name,
        IReadOnlyDictionary<string, string> key,
        CancellationToken cancellationToken = default)
    {
        Record(scope);
        ReferencesAsked.Add(key);

        return Answer(References ?? FakeTableRows.References());
    }

    public Task<TableRowCount> CountExactAsync(
        StudioScope scope,
        string schema,
        string name,
        string? filter = null,
        CancellationToken cancellationToken = default)
    {
        Record(scope);
        CountsAsked.Add(filter);

        return Answer(Count ?? FakeTableRows.Count());
    }

    public Task<TableCellValue> GetCellAsync(
        StudioScope scope,
        string schema,
        string name,
        IReadOnlyDictionary<string, string> key,
        string column,
        CancellationToken cancellationToken = default)
    {
        Record(scope);
        CellsAsked.Add((key, column));

        return Answer(Cell ?? FakeTableRows.Cell(column));
    }

    private Task<T> Answer<T>(T value) => Failure is null ? Task.FromResult(value) : Task.FromException<T>(Failure);

    private void Record(StudioScope scope)
    {
        Reads++;
        LastScope = scope;
        Scopes.Add(scope);
    }
}
