using System.Text;
using System.Text.Json;

namespace KStudio.Core.Decoding;

/// <summary>
/// A record's value as the engine understood it, <b>whole</b> (decision 2.4 of D-PAYLOAD): nothing is cut to a match's budget here, so that
/// the payload rules see all of it and a value clause compares all of it. It is internal to KStudio.Core, never kept by a match and never
/// returned from an API KStudio calls. Under the envelope grant only the kind, the length, the schema id and the CloudEvents attributes are
/// filled in; the bytes, the parsed element and the preview are for a visitor who may read payloads.
/// </summary>
internal sealed record RecordValue
{
    private string? text;

    /// <summary>What it is.</summary>
    public required KafkaValueKind Kind { get; init; }

    /// <summary>How to name it: <c>JSON</c>, <c>CloudEvents (structured), data as JSON</c>, a decoder's name.</summary>
    public required string FormatName { get; init; }

    /// <summary>The length in bytes of the record's value (a CloudEvent's envelope included).</summary>
    public required int Length { get; init; }

    /// <summary>The UTF-8 of a JSON or text value up to <c>MaxDecodeBytes</c>; <see langword="null" /> under the envelope grant and for any other kind.</summary>
    public byte[]? Utf8 { get; init; }

    /// <summary>Whether <see cref="Utf8" /> is shorter than the value because the value is larger than <c>MaxDecodeBytes</c> (text only: larger JSON is undecodable).</summary>
    public bool Cut { get; init; }

    /// <summary>The parsed element of a JSON value; a standalone copy, so no document is held.</summary>
    public JsonElement? Json { get; init; }

    /// <summary>Whether a text value starts like JSON and did not parse: a path clause cannot judge it.</summary>
    public bool LooksLikeJson { get; init; }

    /// <summary>The schema id of a wire-format value.</summary>
    public int? SchemaId { get; init; }

    /// <summary>Hex of the first bytes of a binary or wire-format value; only under the payload grant.</summary>
    public string? Preview { get; init; }

    /// <summary>Why the value is undecodable.</summary>
    public KafkaUndecodableReason? Reason { get; init; }

    /// <summary>The reason a host decoder gave when it refused.</summary>
    public string? ReasonText { get; init; }

    /// <summary>The record's CloudEvents attributes, when it is one.</summary>
    public CloudEventView? CloudEvent { get; init; }

    /// <summary>Whether this is a CloudEvent's data and not the record's value (binary mode: the value is the data; structured: the <c>data</c> member).</summary>
    public bool IsCloudEventData { get; init; }

    /// <summary>Whether a structured CloudEvent has no data: every value clause is false.</summary>
    public bool NoData { get; init; }

    /// <summary>The data of the events after the first in a batch; a clause is true if it is true of any of them.</summary>
    public IReadOnlyList<RecordValue> More { get; init; } = [];

    /// <summary>
    /// A record's ToString writes every member, and this one holds the value (and its text, its parsed element and its hex): a debugger, a
    /// log of the object or an exception that formats it must never put the content in a sink, so only the kind and the length are written.
    /// </summary>
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Kind = ").Append(Kind).Append(", Length = ").Append(Length);
        return true;
    }

    /// <summary>The value's text: the decoded <see cref="Utf8" />.</summary>
    public string Text => text ??= Encoding.UTF8.GetString(Utf8 ?? []);

    /// <summary>This value and the others of a batch.</summary>
    public IEnumerable<RecordValue> All()
    {
        yield return this;

        foreach (RecordValue more in More)
        {
            yield return more;
        }
    }
}
