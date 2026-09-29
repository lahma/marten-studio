using MartenStudio.Services;
using MartenStudio.Services.Database;

namespace MartenStudio.Components.Pages.Database;

/// <summary>
/// The definitions a grid has expanded, fetched once each and on demand.
/// </summary>
/// <remarks>
/// <para>
/// On demand because a definition is a read the gate may refuse - and audit - and a function body can be
/// thousands of lines: a grid of two hundred functions must not ask for two hundred bodies to draw two
/// hundred names. Once each because collapsing and reopening a row is a person looking again, not a
/// reason to ask again.
/// </para>
/// <para>
/// A failed read is not remembered as an answer, so opening the row again tries again. Every failure is a
/// value here - the grid draws it - because an exception escaping a click handler ends the circuit
/// (AGENTS.md hard rule 6).
/// </para>
/// </remarks>
internal sealed class DatabaseDefinitionLoader : IDisposable
{
    private readonly Dictionary<DatabaseObjectRef, DatabaseObjectDefinition> loaded = [];
    private readonly Dictionary<DatabaseObjectRef, string> failures = [];
    private readonly HashSet<DatabaseObjectRef> loading = [];
    private CancellationTokenSource cancellation = new();

    /// <summary>The definition read for <paramref name="reference" />, when there is one.</summary>
    public DatabaseObjectDefinition? Find(DatabaseObjectRef reference) =>
        loaded.TryGetValue(reference, out DatabaseObjectDefinition? definition) ? definition : null;

    /// <summary>Whether a read for <paramref name="reference" /> is in flight.</summary>
    public bool IsLoading(DatabaseObjectRef reference) => loading.Contains(reference);

    /// <summary>Why the last read for <paramref name="reference" /> failed, when it did.</summary>
    public string? FailureOf(DatabaseObjectRef reference) =>
        failures.TryGetValue(reference, out string? failure) ? failure : null;

    /// <summary>Reads <paramref name="reference" />'s definition, unless it has been read or is being read.</summary>
    public async Task LoadAsync(IDatabaseObjectService service, StudioScope? scope, DatabaseObjectRef reference)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(reference);

        if (scope is null || loaded.ContainsKey(reference) || !loading.Add(reference))
        {
            return;
        }

        failures.Remove(reference);
        CancellationToken token = cancellation.Token;

        try
        {
            DatabaseObjectDefinition definition = await service.GetDefinitionAsync(scope, reference, token);

            if (!token.IsCancellationRequested)
            {
                loaded[reference] = definition;
            }
        }
        catch (OperationCanceledException)
        {
            // The grid moved on to another list; this answer belongs to the old one.
        }
        catch (Exception exception)
        {
            if (!token.IsCancellationRequested)
            {
                failures[reference] = exception.Message;
            }
        }
        finally
        {
            loading.Remove(reference);
        }
    }

    /// <summary>Forgets everything, and abandons reads in flight - the list they were for is gone.</summary>
    public void Reset()
    {
        CancellationTokenSource previous = cancellation;
        cancellation = new CancellationTokenSource();
        previous.Cancel();
        previous.Dispose();

        loaded.Clear();
        failures.Clear();
        loading.Clear();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        cancellation.Cancel();
        cancellation.Dispose();
    }
}
