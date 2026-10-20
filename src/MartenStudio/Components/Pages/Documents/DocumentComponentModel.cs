using PostgresStudio.Components.Shared;
using MartenStudio.Services.Documents;
using MartenStudio.Services.Query;
using PostgresStudio.Services.Query;

namespace MartenStudio.Components.Pages.Documents;

/// <summary>
/// The documents components' model helpers: the shared <see cref="ComponentModel" /> reads, and the two URL tokens
/// only the documents screens spell.
/// </summary>
/// <remarks>
/// <see cref="Read{T}" /> and <see cref="ReadList{T}" /> forward to <see cref="ComponentModel" />, where the reasons
/// live. A shared component reads through <see cref="ComponentModel" /> itself; the database browser's and the
/// relationships screen's components still call these until their packets (PS-1b, PS-1d) move them across.
/// </remarks>
internal static class DocumentComponentModel
{
    /// <inheritdoc cref="ComponentModel.Read{T}" />
    public static T Read<T>(object? value, T fallback) => ComponentModel.Read(value, fallback);

    /// <inheritdoc cref="ComponentModel.ReadList{T}" />
    public static IReadOnlyList<T> ReadList<T>(object? value) => ComponentModel.ReadList<T>(value);

    /// <summary>The sort direction as the URL spells it.</summary>
    public static SortDirection Direction(string? token) => DocumentLinks.ParseDirection(token);

    /// <summary>The deleted tri-state as the URL spells it.</summary>
    public static DeletedFilter Deleted(string? token) => DocumentLinks.ParseDeleted(token);
}
