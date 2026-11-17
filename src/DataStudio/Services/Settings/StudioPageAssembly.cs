using System.Reflection;

namespace DataStudio.Services.Settings;

/// <summary>One assembly whose routable components are studio pages, and the compile-time root its <c>@page</c> directives use.</summary>
/// <param name="Assembly">The assembly.</param>
/// <param name="CompileTimeRoot">
/// The rooted prefix every page template in <paramref name="Assembly" /> starts with - <c>/marten</c>, or <c>/</c> for the shell's own pages -
/// which is also the first segment of every page's address: a page's address below the mount is its <c>@page</c> template without the leading
/// slash, always (v2 step 5), so <c>@page "/marten/documents"</c> is served at <c>{mount}/marten/documents</c>.
/// </param>
internal sealed record StudioPageAssembly(Assembly Assembly, string CompileTimeRoot)
{
    /// <summary>
    /// The root's one segment, as written: <c>marten</c> for <c>/marten</c>, and the empty string for the shell's own <c>/</c>. The composition
    /// refuses a root of more than one segment and judges roots by this one (<see cref="StudioModuleComposition" />).
    /// </summary>
    public string Segment => CompileTimeRoot.Trim('/');
}
