using System.Reflection;

namespace DataStudio.Services.Settings;

/// <summary>One assembly whose routable components are studio pages, and the compile-time root its <c>@page</c> directives use.</summary>
/// <param name="Assembly">The assembly.</param>
/// <param name="CompileTimeRoot">
/// The rooted prefix every page template in <paramref name="Assembly" /> starts with - <c>/marten</c> - and which the
/// endpoint mapping re-roots under the configured mount path.
/// </param>
internal sealed record StudioPageAssembly(Assembly Assembly, string CompileTimeRoot)
{
    /// <summary>
    /// Whether the assembly's pages keep their compile-time root as their first relative segment (D34): a module's
    /// <c>@page "/orders/list"</c> is served at <c>{mount}/orders/list</c>, where a host studio's <c>/marten/documents</c> is served
    /// at <c>{mount}/documents</c>. <see langword="false" /> for every host studio's own assemblies.
    /// </summary>
    public bool KeepsRoot { get; init; }
}
