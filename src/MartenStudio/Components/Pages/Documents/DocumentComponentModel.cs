using MartenStudio.Services.Documents;
using MartenStudio.Services.Query;

namespace MartenStudio.Components.Pages.Documents;

/// <summary>
/// Reads a component's model parameter back into the type it really is.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the documents components take <see cref="object"/> for their models.</b> The Razor SDK emits
/// every component class as <c>public</c> — there is no directive or MSBuild switch that makes one
/// internal, and declaring a <c>partial</c> with a different modifier is CS0262. A <c>[Parameter]</c> must
/// be a <c>public</c> property, and a public property may not expose a less accessible type. The document
/// DTOs are internal and must stay internal: AGENTS.md D12 says the data seam is internal and D20 keeps
/// the public snapshot down to two static classes, two classes and one record, so making
/// <c>DocumentPage</c> public to satisfy the compiler would put the whole data layer into the package's
/// contract.
/// </para>
/// <para>
/// So the model crosses the component boundary as <see cref="object"/> and is read back here, while
/// everything else in these components' signatures speaks the vocabulary the URL already uses — a column
/// <em>key</em> rather than a <c>DocumentColumnHeader</c>, <c>"asc"</c>/<c>"desc"</c> rather than
/// <c>SortDirection</c>. That keeps the typed model in exactly one place per component and makes the rest
/// of the boundary something a person can read in a URL.
/// </para>
/// <para>
/// The cost is that one attribute per component is not type-checked by the Razor compiler. It is paid
/// once, at a call site this packet owns, and every use of the value inside the component is fully typed.
/// </para>
/// </remarks>
internal static class DocumentComponentModel
{
    /// <summary>The value as <typeparamref name="T"/>, or <paramref name="fallback"/> when it is not one.</summary>
    public static T Read<T>(object? value, T fallback) => value is T typed ? typed : fallback;

    /// <summary>A list parameter, or an empty list.</summary>
    public static IReadOnlyList<T> ReadList<T>(object? value) =>
        value as IReadOnlyList<T> ?? [];

    /// <summary>The sort direction as the URL spells it.</summary>
    public static SortDirection Direction(string? token) => DocumentLinks.ParseDirection(token);

    /// <summary>The deleted tri-state as the URL spells it.</summary>
    public static DeletedFilter Deleted(string? token) => DocumentLinks.ParseDeleted(token);
}
