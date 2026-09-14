namespace MartenStudio.SampleDomain.Documents;

/// <summary>
/// A document with a payload of about two megabytes, which is what a base64 blob in a JSON column looks
/// like in the wild.
/// </summary>
/// <remarks>
/// <para>
/// It is here because every list view has to prove it does <em>not</em> fetch this. The studio's document
/// list only inlines <c>data::text</c> when <c>octet_length(data::text)</c> is within
/// <c>MaxInlineDocumentBytes</c>, so a collection of these renders as a list of sizes, and only the detail
/// page pays for the bytes. A browser that fetched a page of fifty of these would be transferring a
/// hundred megabytes over a SignalR circuit.
/// </para>
/// <para>
/// The seeder writes exactly one of them, once.
/// </para>
/// </remarks>
public sealed class MediaAsset
{
    public Guid Id { get; set; }

    public string FileName { get; set; } = string.Empty;

    public string ContentType { get; set; } = "image/png";

    /// <summary>How many bytes the payload decodes to, so the document says what it is without decoding.</summary>
    public int ByteCount { get; set; }

    /// <summary>The payload, base64-encoded the way a JSON document has to carry binary.</summary>
    public string Base64 { get; set; } = string.Empty;
}
