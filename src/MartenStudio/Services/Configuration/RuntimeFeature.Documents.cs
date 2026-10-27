using System.Reflection;

using Marten;

namespace MartenStudio.Services.Configuration;

/// <summary>
/// The document screens' light-up: Marten's own preview of what a save would keep, beside the studio's round trip.
/// </summary>
/// <remarks>
/// <para>
/// <b>Read for M5 and not taken, at their tags and at v9.54.2</b> (JasperFx <c>V2.95.1</c>), so a later packet does not
/// read them again. Marten 9.52's <c>IDocumentStoreDiagnosticsWriter.UndeleteDocumentAsync</c> opens
/// <c>LightweightSession(tenantId)</c> - not pinned to the database in scope (hard rule 15) - and re-stores the deleted
/// row through its CLR type before its <c>UndoDeleteWhere</c>, which rewrites <c>data</c> (D7); the studio's own undelete
/// is <c>UndoDeleteWhere</c> alone, on a pinned session, at every Marten. Marten 9.52's metadata ordering
/// (<c>DocumentQueryOptions.OrderByMetadata</c>) is <c>order by mt_last_modified|mt_created_at|mt_version desc nulls last,
/// id</c> in a diagnostics read that runs an exact <c>count(*)</c> per page (D8); the studio's own list orders the same
/// way, keyset-paged, from the floor. Marten 9.53's <c>QueryEventData&lt;T&gt;</c> is <c>QueryRawEventDataOnly&lt;T&gt;</c>
/// (at the floor too), which calls <c>Events.AddEventType&lt;T&gt;()</c> on the host's store and runs Marten's LINQ, and
/// its tenant form opens an unpinned <c>QuerySession(tenantId)</c>. None has a probe here, because the studio uses none.
/// </para>
/// </remarks>
internal sealed partial class RuntimeFeature
{
    /// <summary>
    /// <c>IDocumentStoreDiagnosticsWriter.PreviewDocumentJsonAsync(DocumentWriteRequest, CancellationToken)</c>,
    /// implemented by Marten - JasperFx 2.94.0 declares it as a default interface method that throws
    /// <c>NotSupportedException</c> (jasperfx#1071, <c>src/JasperFx/Documents/IDocumentStoreDiagnosticsWriter.cs</c>), and
    /// Marten 9.53.0 implements it (<c>src/Marten/DocumentStore.DocumentDiagnosticsWriter.cs</c>; absent at v9.52.0, and the
    /// same at v9.54.2). The interface itself is absent from the floor's JasperFx 2.74.0, so it is found by name.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What it does, read at v9.53.0 and v9.54.2.</b> It resolves the type name against the store's mappings - after
    /// <c>Options.Storage.BuildAllMappings()</c>, which builds every lazy mapping in memory - deserializes the JSON with the
    /// store's own serializer, refuses an id in the JSON that differs from the one the request names, and answers
    /// <c>Serializer.ToJson(document)</c>. No session is opened and no connection is made, so it creates and reads
    /// nothing (hard rule 14), and no tenant or database is involved (hard rule 15).
    /// </para>
    /// <para>
    /// <b>What the studio does with it.</b> The edit preview's round trip (D7) is the studio's own, through the same
    /// serializer; Marten's answer is laid beside it, and a disagreement, or a refusal Marten's own save would make, is
    /// flagged in the dialog. (An edit that changes the document's id, which Marten refuses, the studio's own preview
    /// refuses first, at every version.) Below 9.53 the dialog says which Marten it needs.
    /// </para>
    /// <para>
    /// The probe asks for Marten's implementation, not the declaration (<see cref="TenantEventSkipping" />'s shape): a
    /// host that pinned JasperFx 2.94 under Marten 9.52 has only the default, which throws.
    /// </para>
    /// </remarks>
    public static readonly RuntimeFeature DocumentEditPreview = new(
        nameof(DocumentEditPreview),
        RuntimePackage.Marten,
        "9.53.0",
        "9.53.0",
        "The edit preview lays Marten's own preview of a save (PreviewDocumentJsonAsync) beside the studio's round trip, and flags a disagreement.",
        static () => DiagnosticsWriterType() is { } writer
            && writer.IsAssignableFrom(typeof(DocumentStore))
            && ImplementedBy(typeof(DocumentStore), writer, EditPreviewMethod()));

    /// <summary>
    /// JasperFx's <c>IDocumentStoreDiagnosticsWriter</c>, or <see langword="null" /> where this JasperFx has none (the
    /// floor's 2.74.0 has none; 2.90.0 has it).
    /// </summary>
    internal static Type? DiagnosticsWriterType() =>
        typeof(JasperFx.AutoCreate).Assembly.GetType("JasperFx.Documents.IDocumentStoreDiagnosticsWriter", throwOnError: false);

    /// <summary>
    /// The declaration of <c>PreviewDocumentJsonAsync(DocumentWriteRequest, CancellationToken)</c> on the writer interface,
    /// or <see langword="null" /> where the interface or the member is absent.
    /// </summary>
    internal static MethodInfo? EditPreviewMethod()
    {
        Type? writer = DiagnosticsWriterType();
        Type? request = writer?.Assembly.GetType("JasperFx.Documents.DocumentWriteRequest", throwOnError: false);

        return writer is null || request is null
            ? null
            : writer.GetMethod(
                "PreviewDocumentJsonAsync",
                BindingFlags.Public | BindingFlags.Instance,
                [request, typeof(CancellationToken)]);
    }
}
