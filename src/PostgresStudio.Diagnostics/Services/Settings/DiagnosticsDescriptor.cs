using System.Reflection;

using PostgresStudio.Services.Sources;

namespace PostgresStudio.Services.Settings;

/// <summary>
/// One assembly whose health checks a studio runs, and the kinds of source they may run against.
/// </summary>
/// <param name="Assembly">The assembly the registry discovers checks in.</param>
/// <param name="SourceKinds">
/// The kinds of source its checks are offered, or <see langword="null" /> for every kind. A check that reads a source
/// through a studio's own types - MartenStudio's checks read the store, its options and its daemon - names that studio's
/// kind here, and is then never run against any other: no row, not a "could not check" one.
/// </param>
internal sealed record StudioHealthCheckAssembly(Assembly Assembly, IReadOnlySet<StudioSourceKind>? SourceKinds = null);

/// <summary>
/// What makes one studio this studio, as far as the diagnostics engine reads it: its key, its name, the assemblies its
/// health checks are discovered in and how it names a source.
/// </summary>
/// <remarks>
/// A singleton, registered by <c>AddPostgresDiagnostics()</c> or by a studio's <c>Add*Studio()</c>, whose
/// <see cref="DiagnosticsDescriptor" /> is one of these with the studio's pages, navigation and authorization resource added.
/// Nothing on it is about a visitor and nothing on it does IO.
/// </remarks>
internal class DiagnosticsDescriptor
{
    /// <summary>The studio's key: <c>marten</c>. Also what <c>StudioRegistrationMarker</c> holds.</summary>
    public required string Key { get; init; }

    /// <summary>The product's name in a sentence: <c>Marten Studio</c>.</summary>
    public required string ProductName { get; init; }

    /// <summary>
    /// The assemblies the health registry discovers checks in, each with the kinds of source its checks may run against.
    /// A check found in more than one entry runs only against a kind every one of them admits - which is what keeps a
    /// studio's own checks off another studio's sources while both are compiled into one assembly.
    /// </summary>
    public required IReadOnlyList<StudioHealthCheckAssembly> HealthCheckAssemblies { get; init; }

    /// <summary>The query-string name the source key travels under: <c>store</c>.</summary>
    public required string SourceQueryParameter { get; init; }

    /// <summary>A source in a sentence: <c>Marten store</c>.</summary>
    public required string SourceNoun { get; init; }

    /// <summary>A source as a control's label: <c>Store</c>.</summary>
    public required string SourceLabel { get; init; }

    /// <summary>
    /// Who runs the check codes <c>AcceptedChecks</c> may name, as the refusal of an unknown one says it: <c>of this studio</c>
    /// for a studio, so no studio host's sentence changes, and for a host with no studio, whose engine is all there is, the
    /// package that runs them.
    /// </summary>
    public string CheckCodeOwner { get; init; } = "of this studio";

    /// <summary>
    /// The check codes this studio once ran and no longer does, each with the release that retired it (<c>K10</c> to
    /// <c>1.1</c>): plain strings, compared ignoring case. An <c>AcceptedChecks</c> entry for one is accepted and ignored, with
    /// one Information line saying so, so that a host that accepted a check never fails to start because a release retired it.
    /// A code that is neither registered nor listed here is still refused. Empty unless the studio retired a check of its own.
    /// </summary>
    public IReadOnlyDictionary<string, string> RetiredCheckCodes { get; init; } = new Dictionary<string, string>();
}
