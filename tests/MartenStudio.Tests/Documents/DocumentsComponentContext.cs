using MartenStudio.Services.Documents;
using MartenStudio.Tests.Components;
using MartenStudio.Tests.Support;

using Microsoft.Extensions.DependencyInjection;

namespace MartenStudio.Tests.Documents;

/// <summary>
/// The shared studio component context plus the three services the documents pages inject.
/// </summary>
/// <remarks>
/// <para>
/// A subclass rather than an edit to <see cref="StudioComponentContext" />: the shared fixture is what
/// every area's tests build on, and each area adding its own fake to it would make the base class grow one
/// packet at a time.
/// </para>
/// <para>
/// It is a subclass rather than a <c>StudioComponentContext(configure:)</c> call because the two are the
/// same thing here — the base constructor resolves nothing, so registering after it runs is legal either
/// way — and this shape is the one that can carry the three typed <c>Data</c>/<c>Writes</c>/<c>Browser</c>
/// properties the tests read their recordings off. The <c>configure</c> hook stays the way a one-off test
/// adds a service without a context of its own.
/// </para>
/// </remarks>
internal sealed class DocumentsComponentContext : StudioComponentContext
{
    public DocumentsComponentContext()
    {
        Services.AddSingleton<IDocumentDataService>(Data);
        Services.AddSingleton<IDocumentWriteService>(Writes);
        Services.AddSingleton(Browser);

        // The pages ask the browser what it remembered. The base context's loose JS runtime answers the
        // default for every call - null for `prefs.get`, which is exactly "nothing pinned" and "no stored
        // columns" - and records the invocation either way, which is what the copy tests assert on.
        WithStores("default");
    }

    /// <summary>What the pages read.</summary>
    public FakeDocumentDataService Data { get; } = new();

    /// <summary>What the pages write through, and the record of what they asked it to do.</summary>
    public FakeDocumentWriteService Writes { get; } = new();

    /// <summary>What the circuit remembers between the list and the detail page.</summary>
    public DocumentBrowserState Browser { get; } = new();
}
