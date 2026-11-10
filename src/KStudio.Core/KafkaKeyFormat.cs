using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace KStudio;

/// <summary>
/// How a topic's record keys are written, so that a search literal can be compiled to the bytes a producer wrote and compared
/// without decoding the key (AGENTS.md D39).
/// </summary>
/// <remarks>
/// A format returns <b>every byte form</b> a producer could have written for a literal: <c>=</c> compares against each of them.
/// The <c>problem</c> a format reports never contains the literal, because an error message is a sink like any other (D40).
/// </remarks>
public abstract class KafkaKeyFormat
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Names the format.</summary>
    /// <param name="name">The format's name, shown in verdicts.</param>
    protected KafkaKeyFormat(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
    }

    /// <summary>Keys that are UTF-8 text. The default for a topic that declares nothing.</summary>
    public static KafkaKeyFormat Utf8String { get; } = new Utf8Format();

    /// <summary>Keys that are JSON strings (a producer's serializer wrote the quotes and the escapes).</summary>
    public static KafkaKeyFormat JsonString { get; } = new JsonStringFormat();

    /// <summary>Keys that are 8-byte big-endian integers (<c>Serializers.Int64</c>).</summary>
    public static KafkaKeyFormat Int64BigEndian { get; } = new IntegerFormat("Int64BigEndian", 8);

    /// <summary>Keys that are 4-byte big-endian integers.</summary>
    public static KafkaKeyFormat Int32BigEndian { get; } = new IntegerFormat("Int32BigEndian", 4);

    /// <summary>Keys that are 16-byte GUIDs in RFC 4122 (big-endian) order.</summary>
    public static KafkaKeyFormat GuidRfc4122 { get; } = new GuidFormat("GuidRfc4122", bigEndian: true);

    /// <summary>Keys that are 16-byte GUIDs in .NET's mixed-endian order (<c>Guid.ToByteArray()</c>).</summary>
    public static KafkaKeyFormat GuidDotNet { get; } = new GuidFormat("GuidDotNet", bigEndian: false);

    /// <summary>Keys that are raw bytes; a search literal is hexadecimal.</summary>
    public static KafkaKeyFormat Bytes { get; } = new BytesFormat();

    /// <summary>The format's name.</summary>
    public string Name { get; }

    /// <summary>Whether <c>^=</c> (a byte prefix) can be compiled for this format. <see langword="false" /> unless overridden.</summary>
    public virtual bool SupportsPrefix => false;

    /// <summary>Compiles a search literal to the byte forms a producer could have written for it.</summary>
    /// <param name="literal">The literal as typed.</param>
    /// <param name="forms">The byte forms, when the literal is valid.</param>
    /// <param name="problem">What is wrong, never containing the literal.</param>
    /// <returns>Whether the literal is valid for this format.</returns>
    public abstract bool TryEncode(string literal, out IReadOnlyList<byte[]> forms, out string? problem);

    /// <summary>Compiles a search literal to the byte prefixes a producer's keys could start with.</summary>
    /// <param name="literal">The literal as typed.</param>
    /// <param name="forms">The byte prefixes, when the literal is valid.</param>
    /// <param name="problem">What is wrong, never containing the literal.</param>
    /// <returns>Whether the literal is valid for this format; <see langword="false" /> unless <see cref="SupportsPrefix" />.</returns>
    public virtual bool TryEncodePrefix(string literal, out IReadOnlyList<byte[]> forms, out string? problem)
    {
        forms = [];
        problem = "a prefix needs a text key format, and this topic's keys are " + Name;
        return false;
    }

    /// <summary>Formats a key's bytes as text, for display and for <c>~</c>.</summary>
    /// <param name="key">The key's bytes.</param>
    /// <param name="text">The key as text, when it is one.</param>
    /// <returns>Whether the key is valid for this format.</returns>
    public abstract bool TryFormat(ReadOnlySpan<byte> key, out string? text);

    private static List<byte[]> Distinct(IEnumerable<byte[]> forms)
    {
        List<byte[]> result = [];

        foreach (byte[] form in forms)
        {
            if (!result.Any(x => x.AsSpan().SequenceEqual(form)))
            {
                result.Add(form);
            }
        }

        return result;
    }

    private sealed class Utf8Format() : KafkaKeyFormat("Utf8String")
    {
        public override bool SupportsPrefix => true;

        public override bool TryEncode(string literal, out IReadOnlyList<byte[]> forms, out string? problem)
        {
            forms = [Encoding.UTF8.GetBytes(literal)];
            problem = null;
            return true;
        }

        public override bool TryEncodePrefix(string literal, out IReadOnlyList<byte[]> forms, out string? problem) =>
            TryEncode(literal, out forms, out problem);

        public override bool TryFormat(ReadOnlySpan<byte> key, out string? text)
        {
            try
            {
                text = StrictUtf8.GetString(key);
                return true;
            }
            catch (DecoderFallbackException)
            {
                text = null;
                return false;
            }
        }
    }

    private sealed class JsonStringFormat() : KafkaKeyFormat("JsonString")
    {
        public override bool SupportsPrefix => true;

        public override bool TryEncode(string literal, out IReadOnlyList<byte[]> forms, out string? problem)
        {
            forms = Distinct(Quoted(literal).Select(static x => Encoding.UTF8.GetBytes(x + "\"")));
            problem = null;
            return true;
        }

        public override bool TryEncodePrefix(string literal, out IReadOnlyList<byte[]> forms, out string? problem)
        {
            forms = Distinct(Quoted(literal).Select(static x => Encoding.UTF8.GetBytes(x)));
            problem = null;
            return true;
        }

        public override bool TryFormat(ReadOnlySpan<byte> key, out string? text)
        {
            text = null;

            if (key.Length >= 2 && key[0] == (byte) '"')
            {
                try
                {
                    Utf8JsonReader reader = new(key);

                    if (reader.Read() && reader.TokenType == JsonTokenType.String)
                    {
                        text = reader.GetString();
                        return text is not null;
                    }
                }
                catch (JsonException)
                {
                    // Not JSON after all: fall through to plain text below.
                }
            }

            return new Utf8Format().TryFormat(key, out text);
        }

        /// <summary>The opening quote and the escaped text, without the closing quote: minimal escaping, then the default encoder's (F8).</summary>
        private static IEnumerable<string> Quoted(string literal)
        {
            StringBuilder minimal = new("\"");

            foreach (char c in literal)
            {
                switch (c)
                {
                    case '"': minimal.Append("\\\""); break;
                    case '\\': minimal.Append("\\\\"); break;
                    case '\b': minimal.Append("\\b"); break;
                    case '\f': minimal.Append("\\f"); break;
                    case '\n': minimal.Append("\\n"); break;
                    case '\r': minimal.Append("\\r"); break;
                    case '\t': minimal.Append("\\t"); break;
                    case < ' ': minimal.Append("\\u").Append(((int) c).ToString("x4", CultureInfo.InvariantCulture)); break;
                    default: minimal.Append(c); break;
                }
            }

            yield return minimal.ToString();
            yield return "\"" + JsonEncodedText.Encode(literal).Value;
        }
    }

    private sealed class IntegerFormat(string name, int width) : KafkaKeyFormat(name)
    {
        public override bool TryEncode(string literal, out IReadOnlyList<byte[]> forms, out string? problem)
        {
            forms = [];
            problem = null;

            if (width == 8 && long.TryParse(literal, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long value))
            {
                byte[] bytes = new byte[8];
                BinaryPrimitives.WriteInt64BigEndian(bytes, value);
                forms = [bytes];
                return true;
            }

            if (width == 4 && int.TryParse(literal, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int small))
            {
                byte[] bytes = new byte[4];
                BinaryPrimitives.WriteInt32BigEndian(bytes, small);
                forms = [bytes];
                return true;
            }

            problem = "expected a whole number that fits in " + width.ToString(CultureInfo.InvariantCulture) + " bytes";
            return false;
        }

        public override bool TryFormat(ReadOnlySpan<byte> key, out string? text)
        {
            text = null;

            if (key.Length != width)
            {
                return false;
            }

            text = width == 8
                ? BinaryPrimitives.ReadInt64BigEndian(key).ToString(CultureInfo.InvariantCulture)
                : BinaryPrimitives.ReadInt32BigEndian(key).ToString(CultureInfo.InvariantCulture);
            return true;
        }
    }

    private sealed class GuidFormat(string name, bool bigEndian) : KafkaKeyFormat(name)
    {
        public override bool TryEncode(string literal, out IReadOnlyList<byte[]> forms, out string? problem)
        {
            if (Guid.TryParse(literal, out Guid value))
            {
                forms = [value.ToByteArray(bigEndian)];
                problem = null;
                return true;
            }

            forms = [];
            problem = "expected a GUID";
            return false;
        }

        public override bool TryFormat(ReadOnlySpan<byte> key, out string? text)
        {
            if (key.Length == 16)
            {
                text = new Guid(key, bigEndian).ToString();
                return true;
            }

            text = null;
            return false;
        }
    }

    private sealed class BytesFormat() : KafkaKeyFormat("Bytes")
    {
        public override bool SupportsPrefix => true;

        public override bool TryEncode(string literal, out IReadOnlyList<byte[]> forms, out string? problem)
        {
            if (TryHex(literal, out byte[] bytes))
            {
                forms = [bytes];
                problem = null;
                return true;
            }

            forms = [];
            problem = "expected hexadecimal digits in pairs, optionally starting with 0x";
            return false;
        }

        public override bool TryEncodePrefix(string literal, out IReadOnlyList<byte[]> forms, out string? problem) =>
            TryEncode(literal, out forms, out problem);

        public override bool TryFormat(ReadOnlySpan<byte> key, out string? text)
        {
            text = "0x" + Convert.ToHexString(key).ToLowerInvariant();
            return true;
        }

        private static bool TryHex(string literal, out byte[] bytes)
        {
            string digits = literal.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? literal[2..] : literal;
            bytes = [];

            if (digits.Length == 0 || digits.Length % 2 != 0)
            {
                return false;
            }

            try
            {
                bytes = Convert.FromHexString(digits);
                return true;
            }
            catch (FormatException)
            {
                return false;
            }
        }
    }
}
