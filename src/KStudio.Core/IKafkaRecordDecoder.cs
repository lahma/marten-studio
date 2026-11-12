namespace KStudio;

/// <summary>
/// Decodes a topic's record values into something the studio can show and search (AGENTS.md D39, decision 8). It runs on the scan's own
/// thread, so it must be fast and must not wait on I/O (a decoder that needs a schema caches it). It never returns a CLR type: it returns
/// JSON bytes or text, which the engine parses itself under its own size and depth caps.
/// </summary>
public interface IKafkaRecordDecoder
{
    /// <summary>The decoder's name, shown as the value's format.</summary>
    string Name { get; }

    /// <summary>Decodes one value. Never called for a tombstone.</summary>
    /// <param name="context">The record.</param>
    /// <returns>The result: not applicable, JSON, text or undecodable.</returns>
    KafkaDecodeResult Decode(KafkaDecodeContext context);
}

/// <summary>A record header as a decoder sees it.</summary>
/// <param name="Name">The header's name.</param>
/// <param name="Value">The value, or <see langword="null" />.</param>
public sealed record KafkaHeader(string Name, ReadOnlyMemory<byte>? Value);

/// <summary>The record a decoder is asked to decode.</summary>
public sealed class KafkaDecodeContext
{
    internal KafkaDecodeContext(string topic, int partition, long offset, ReadOnlyMemory<byte> value, IReadOnlyList<KafkaHeader> headers)
    {
        Topic = topic;
        Partition = partition;
        Offset = offset;
        Value = value;
        Headers = headers;
    }

    /// <summary>The topic.</summary>
    public string Topic { get; }

    /// <summary>The partition.</summary>
    public int Partition { get; }

    /// <summary>The offset.</summary>
    public long Offset { get; }

    /// <summary>The value's bytes. Never a tombstone; possibly empty.</summary>
    public ReadOnlyMemory<byte> Value { get; }

    /// <summary>The record's headers, in wire order, without those a redaction rule masks (the built-in deny-list included).</summary>
    public IReadOnlyList<KafkaHeader> Headers { get; }
}

/// <summary>What a decoder returns.</summary>
public sealed class KafkaDecodeResult
{
    private KafkaDecodeResult(KafkaDecodeResultKind kind, ReadOnlyMemory<byte> json, string? text, string? formatName)
    {
        Kind = kind;
        JsonBytes = json;
        TextValue = text;
        FormatName = formatName;
    }

    /// <summary>This decoder does not handle this record; the engine goes on to the next step of its detection order.</summary>
    public static KafkaDecodeResult NotApplicable { get; } = new(KafkaDecodeResultKind.NotApplicable, default, null, null);

    internal KafkaDecodeResultKind Kind { get; }

    internal ReadOnlyMemory<byte> JsonBytes { get; }

    internal string? TextValue { get; }

    internal string? FormatName { get; }

    /// <summary>The value is JSON, as UTF-8 bytes. The engine parses it itself, under its own caps.</summary>
    /// <param name="utf8Json">The JSON text.</param>
    /// <param name="formatName">What to show as the format; the decoder's name when <see langword="null" />.</param>
    public static KafkaDecodeResult Json(ReadOnlyMemory<byte> utf8Json, string? formatName = null) =>
        new(KafkaDecodeResultKind.Json, utf8Json, null, formatName);

    /// <summary>The value is text.</summary>
    /// <param name="text">The text.</param>
    /// <param name="formatName">What to show as the format; the decoder's name when <see langword="null" />.</param>
    public static KafkaDecodeResult Text(string text, string? formatName = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new(KafkaDecodeResultKind.Text, default, text, formatName);
    }

    /// <summary>The decoder handles this record but cannot decode it. The reason is shown, so it must not contain payload.</summary>
    /// <param name="reason">Why.</param>
    public static KafkaDecodeResult Undecodable(string reason)
    {
        ArgumentNullException.ThrowIfNull(reason);
        return new(KafkaDecodeResultKind.Undecodable, default, reason, null);
    }
}

internal enum KafkaDecodeResultKind
{
    NotApplicable,
    Json,
    Text,
    Undecodable,
}

/// <summary>
/// The decoders the engine itself provides, which a host may name to force one for a topic. Without a declared decoder the engine detects
/// the format of every value on its own.
/// </summary>
public static class KafkaDecoders
{
    /// <summary>Values are CloudEvents in structured mode (a JSON envelope), whatever the <c>content-type</c> header says.</summary>
    public static IKafkaRecordDecoder CloudEvents { get; } = new BuiltIn("CloudEvents (structured)", KafkaBuiltInDecoder.CloudEvents);

    /// <summary>Values are JSON.</summary>
    public static IKafkaRecordDecoder Json { get; } = new BuiltIn("JSON", KafkaBuiltInDecoder.Json);

    /// <summary>Values are UTF-8 text.</summary>
    public static IKafkaRecordDecoder Text { get; } = new BuiltIn("Text", KafkaBuiltInDecoder.Text);

    internal static KafkaBuiltInDecoder? BuiltInKind(IKafkaRecordDecoder? decoder) => decoder is BuiltIn builtIn ? builtIn.Kind : null;

    private sealed class BuiltIn(string name, KafkaBuiltInDecoder kind) : IKafkaRecordDecoder
    {
        public string Name { get; } = name;

        public KafkaBuiltInDecoder Kind { get; } = kind;

        // The engine recognises these by reference and never calls Decode; if a host calls it itself, it says it does not apply.
        public KafkaDecodeResult Decode(KafkaDecodeContext context) => KafkaDecodeResult.NotApplicable;
    }
}

internal enum KafkaBuiltInDecoder
{
    CloudEvents,
    Json,
    Text,
}
