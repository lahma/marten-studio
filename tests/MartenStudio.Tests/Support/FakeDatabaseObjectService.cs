using MartenStudio.Services;
using MartenStudio.Services.Database;

namespace MartenStudio.Tests.Support;

/// <summary>
/// What the database browser's pages are given, said outright by a test.
/// </summary>
/// <remarks>
/// A hand-written fake rather than a mocking framework (AGENTS.md package budget). Every answer is a
/// settable property; every call is recorded, so a page test can assert which scope and which query a
/// page asked with. <see cref="Failure" /> makes every method throw, for the page's error frame - the real
/// service never throws but for cancellation, so a page that handles a thrown exception is handling the
/// unexpected, which is exactly what that frame is for.
/// </remarks>
internal sealed class FakeDatabaseObjectService : IDatabaseObjectService
{
    /// <summary>What <see cref="GetOverviewAsync" /> answers.</summary>
    public DatabaseBrowserOverview Overview { get; set; } = FakeDatabaseObjects.Overview();

    /// <summary>
    /// What <see cref="ListAsync" /> answers. <see langword="null" /> - the default - answers from
    /// <see cref="FakeDatabaseObjects.List" /> for whatever was asked, so the query is honoured.
    /// </summary>
    public DatabaseObjectList? List { get; set; }

    /// <summary>
    /// What <see cref="GetObjectAsync" /> answers. <see langword="null" /> - the default - answers from
    /// <see cref="FakeDatabaseObjects.Detail" /> for the name asked.
    /// </summary>
    public DatabaseObjectDetail? Detail { get; set; }

    /// <summary>
    /// What <see cref="GetDefinitionAsync" /> answers. <see langword="null" /> - the default - answers from
    /// <see cref="FakeDatabaseObjects.Definition" /> for the object asked.
    /// </summary>
    public DatabaseObjectDefinition? Definition { get; set; }

    /// <summary>What every method throws, when a test is about the failure frame.</summary>
    public Exception? Failure { get; set; }

    /// <summary>How many calls of any kind have been made.</summary>
    public int Reads { get; private set; }

    /// <summary>The scope the last call was made with.</summary>
    public StudioScope? LastScope { get; private set; }

    /// <summary>Every query <see cref="ListAsync" /> was asked, in order.</summary>
    public List<DatabaseObjectQuery> Queries { get; } = [];

    /// <summary>Every object <see cref="GetObjectAsync" /> was asked about, as <c>schema.name</c>.</summary>
    public List<string> ObjectsAsked { get; } = [];

    /// <summary>Every definition <see cref="GetDefinitionAsync" /> was asked for.</summary>
    public List<DatabaseObjectRef> DefinitionsAsked { get; } = [];

    public Task<DatabaseBrowserOverview> GetOverviewAsync(StudioScope scope, CancellationToken cancellationToken = default)
    {
        Record(scope);

        return Failure is null ? Task.FromResult(Overview) : Task.FromException<DatabaseBrowserOverview>(Failure);
    }

    public Task<DatabaseObjectList> ListAsync(
        StudioScope scope,
        DatabaseObjectQuery query,
        CancellationToken cancellationToken = default)
    {
        Record(scope);
        Queries.Add(query);

        return Failure is null
            ? Task.FromResult(List ?? FakeDatabaseObjects.List(query))
            : Task.FromException<DatabaseObjectList>(Failure);
    }

    public Task<DatabaseObjectDetail> GetObjectAsync(
        StudioScope scope,
        string schema,
        string name,
        CancellationToken cancellationToken = default)
    {
        Record(scope);
        ObjectsAsked.Add(schema + "." + name);

        return Failure is null
            ? Task.FromResult(Detail ?? FakeDatabaseObjects.Detail(schema, name))
            : Task.FromException<DatabaseObjectDetail>(Failure);
    }

    public Task<DatabaseObjectDefinition> GetDefinitionAsync(
        StudioScope scope,
        DatabaseObjectRef reference,
        CancellationToken cancellationToken = default)
    {
        Record(scope);
        DefinitionsAsked.Add(reference);

        return Failure is null
            ? Task.FromResult(Definition ?? FakeDatabaseObjects.Definition(reference))
            : Task.FromException<DatabaseObjectDefinition>(Failure);
    }

    private void Record(StudioScope scope)
    {
        Reads++;
        LastScope = scope;
    }
}
