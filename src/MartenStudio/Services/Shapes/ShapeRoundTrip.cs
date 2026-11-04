using System.Text;
using Marten;
using Marten.Schema;

namespace MartenStudio.Services.Shapes;

/// <summary>What the round trip of one stored document found.</summary>
/// <param name="TypeName">The concrete type's short name; the collection's root type for an unknown subclass.</param>
/// <param name="FullTypeName">The concrete type's full name.</param>
/// <param name="Failure">Why the document could not be checked, or <see langword="null" />.</param>
/// <param name="Members">What the document and its round trip disagree about; empty when it failed.</param>
internal sealed record ShapeItemResult(
    string TypeName,
    string? FullTypeName,
    ShapeFailure? Failure,
    IReadOnlyList<ShapeMember> Members);

/// <summary>
/// One stored document through the store's own serializer and back (D-SHAPE S5): <c>FromJson(type, stream)</c> into the row's
/// concrete type, <c>ToJson</c> of the result, and a <see cref="ShapeDiff" /> of the two.
/// </summary>
/// <remarks>
/// <see cref="MartenStudio.Services.Documents.DocumentWriteService" />'s deserialization path, in a helper of this folder's own so that the write service is never
/// edited for a read: there is no string overload on Marten's serializer, so the JSON goes in as UTF-8 over a
/// <see cref="MemoryStream" />. Nothing here keeps an exception message.
/// </remarks>
internal static class ShapeRoundTrip
{
    /// <summary>Round-trips one document.</summary>
    /// <param name="serializer">The store's serializer, <c>store.Options.Serializer()</c>.</param>
    /// <param name="documentType">The collection's mapping.</param>
    /// <param name="rowAlias">The row's <c>mt_doc_type</c>, when the table has the column; <see langword="null" /> otherwise.</param>
    /// <param name="storedJson">The row's <c>data</c> as text.</param>
    /// <param name="compare">The comparison, for a test that needs it to throw; <see cref="ShapeDiff.Compare(string, string)" /> otherwise.</param>
    public static ShapeItemResult Check(
        ISerializer serializer,
        IDocumentType documentType,
        string? rowAlias,
        string storedJson,
        Func<string, string, IReadOnlyList<ShapeMember>>? compare = null)
    {
        ArgumentNullException.ThrowIfNull(serializer);
        ArgumentNullException.ThrowIfNull(documentType);
        ArgumentNullException.ThrowIfNull(storedJson);

        Type type = documentType.DocumentType;

        if (!string.IsNullOrWhiteSpace(rowAlias))
        {
            try
            {
                type = documentType.TypeFor(rowAlias);
            }
            catch (ArgumentOutOfRangeException)
            {
                return new ShapeItemResult(
                    documentType.DocumentType.Name,
                    documentType.DocumentType.FullName,
                    ShapeFailureDescriber.Of(ShapeFailureKind.UnknownSubclass, nameof(ArgumentOutOfRangeException), rowAlias),
                    []);
            }
        }

        string name = type.Name;
        string? fullName = type.FullName;

        object? document;

        try
        {
            using MemoryStream stream = new(Encoding.UTF8.GetBytes(storedJson), writable: false);
            document = serializer.FromJson(type, stream);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new ShapeItemResult(
                name, fullName, ShapeFailureDescriber.Describe(ShapeFailureKind.Unreadable, exception, storedJson), []);
        }

        if (document is null)
        {
            return new ShapeItemResult(name, fullName, ShapeFailureDescriber.Of(ShapeFailureKind.ReadAsNothing, string.Empty), []);
        }

        string roundTripped;

        try
        {
            roundTripped = serializer.ToJson(document);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new ShapeItemResult(
                name, fullName, ShapeFailureDescriber.Describe(ShapeFailureKind.NotWritable, exception, storedJson), []);
        }

        try
        {
            return new ShapeItemResult(name, fullName, null, (compare ?? ((stored, read) => ShapeDiff.Compare(stored, read)))(storedJson, roundTripped));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Nested deeper than the canonicalizer reads, or anything else one odd document does to the comparison: the item loaded,
            // the comparison could not be made, and that is this item's finding - by the exception's type name alone - and not the
            // check's failure.
            return new ShapeItemResult(
                name, fullName, ShapeFailureDescriber.Of(ShapeFailureKind.NotWritable, exception.GetType().Name), []);
        }
    }
}
