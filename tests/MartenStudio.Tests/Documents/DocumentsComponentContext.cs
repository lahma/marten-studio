using MartenStudio.Services.Documents;
using MartenStudio.Tests.Components;
using MartenStudio.Tests.Support;

using Microsoft.Extensions.DependencyInjection;

namespace MartenStudio.Tests.Documents;

/// <summary>
/// The shared studio component context plus the two services the documents pages inject.
/// </summary>
/// <remarks>
/// A subclass rather than an edit to <see cref="StudioComponentContext" />: the shared fixture is what
/// every area's tests build on, and each area adding its own fake to it would make the base class grow one
/// packet at a time.
/// </remarks>
internal sealed class DocumentsComponentContext : StudioComponentContext
{
    public DocumentsComponentContext()
    {
        Services.AddSingleton<IDocumentDataService>(Data);
        Services.AddSingleton(Browser);

        // The pages ask the browser what it remembered. The base context's loose JS runtime answers the
        // default for every call - null for `prefs.get`, which is exactly "nothing pinned" and "no stored
        // columns" - and records the invocation either way, which is what the copy tests assert on.
        WithStores("default");
    }

    /// <summary>What the pages read.</summary>
    public FakeDocumentDataService Data { get; } = new();

    /// <summary>What the circuit remembers between the list and the detail page.</summary>
    public DocumentBrowserState Browser { get; } = new();
}
