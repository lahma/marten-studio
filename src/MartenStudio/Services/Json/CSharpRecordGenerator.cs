using System.Globalization;
using System.Text;
using System.Text.Json;

namespace MartenStudio.Services.Json;

/// <summary>
/// Turns a sample document into the C# records a Marten document type would need.
/// </summary>
/// <remarks>
/// <para>
/// This exists for the collection the studio found in the database that the host never registered: the
/// JSON is there, the CLR type is not, and the first thing anyone wants is a type to deserialize it
/// with. The output is a skeleton and is honest about being one - nullability is inferred from what the
/// sampled documents actually contained, and a member whose samples disagree comes out as
/// <see cref="JsonElement"/> rather than as a guess that compiles and then throws.
/// </para>
/// <para>
/// Output is deterministic: member order follows first appearance, nested records are emitted in the
/// order they are discovered, and nothing depends on a hash ordering. That is what lets the tests pin it
/// with golden strings.
/// </para>
/// </remarks>
internal static class CSharpRecordGenerator
{
    private static readonly JsonDocumentOptions ParseOptions = new() { MaxDepth = 64 };

    /// <summary>Generates the record skeleton for <paramref name="json"/>.</summary>
    /// <param name="json">A sample document, or an array of sample documents.</param>
    /// <param name="typeName">The name for the root record.</param>
    public static string Generate(string? json, string typeName)
    {
        var rootName = Identifier(typeName, "Document");

        if (string.IsNullOrWhiteSpace(json))
        {
            return "// There is no document to generate a record from.";
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, ParseOptions);
        }
        catch (JsonException ex)
        {
            return "// This is not valid JSON: " + ex.Message;
        }

        using (document)
        {
            var shape = new Shape();
            var root = document.RootElement;

            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in root.EnumerateArray())
                {
                    Observe(shape, item);
                }
            }
            else
            {
                Observe(shape, root);
            }

            if (!shape.SawObject)
            {
                return "// A record needs an object; this document is " + Article(root.ValueKind) + ".";
            }

            var writer = new Emitter();
            writer.Emit(shape, rootName);
            return writer.ToString();
        }
    }

    private static string Article(JsonValueKind kind) => kind switch
    {
        JsonValueKind.Array => "an array of non-objects",
        JsonValueKind.String => "a string",
        JsonValueKind.Number => "a number",
        JsonValueKind.True or JsonValueKind.False => "a boolean",
        JsonValueKind.Null => "null",
        _ => "not an object",
    };

    /// <summary>Folds one sample into the shape being learned.</summary>
    private static void Observe(Shape shape, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                shape.SawObject = true;
                shape.ObjectSamples++;
                foreach (var property in element.EnumerateObject())
                {
                    if (!shape.Members.TryGetValue(property.Name, out var member))
                    {
                        member = new Member();
                        shape.Members[property.Name] = member;
                        shape.MemberOrder.Add(property.Name);
                    }

                    member.SeenIn++;
                    Observe(member.Shape, property.Value);
                }

                break;

            case JsonValueKind.Array:
                shape.SawArray = true;
                shape.Element ??= new Shape();
                foreach (var item in element.EnumerateArray())
                {
                    Observe(shape.Element, item);
                }

                break;

            case JsonValueKind.String:
                shape.SawString = true;
                shape.StringSamples++;
                switch (JsonModelBuilder.DetectSemantic(element.GetString() ?? string.Empty, int.MaxValue))
                {
                    case JsonSemanticKind.Guid:
                        shape.GuidStrings++;
                        break;
                    case JsonSemanticKind.DateTime:
                        shape.DateStrings++;
                        break;
                    default:
                        break;
                }

                break;

            case JsonValueKind.Number:
                shape.SawNumber = true;
                ObserveNumber(shape, element.GetRawText());
                break;

            case JsonValueKind.True:
            case JsonValueKind.False:
                shape.SawBoolean = true;
                break;

            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
            default:
                shape.SawNull = true;
                break;
        }
    }

    private static void ObserveNumber(Shape shape, string raw)
    {
        if (raw.Contains('.', StringComparison.Ordinal) ||
            raw.Contains('e', StringComparison.OrdinalIgnoreCase))
        {
            shape.SawFractional = true;
            if (!decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            {
                shape.SawWideNumber = true;
            }

            return;
        }

        if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integral))
        {
            if (integral is < int.MinValue or > int.MaxValue)
            {
                shape.SawLongNumber = true;
            }

            return;
        }

        shape.SawWideNumber = true;
    }

    /// <summary>Makes <paramref name="candidate"/> into a C# identifier, PascalCased.</summary>
    /// <remarks>
    /// There is deliberately no <c>@</c>-escaping branch here. Every C# keyword is lower case and every
    /// result of this method starts with an upper-case letter or an underscore, so a member named
    /// <c>class</c> comes out as <c>Class</c> and no keyword can ever be produced. The branch that used to
    /// check a keyword table was unreachable, and an unreachable guard is worse than none: it reads as if
    /// the case were handled.
    /// </remarks>
    internal static string Identifier(string? candidate, string fallback)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return fallback;
        }

        var builder = new StringBuilder();
        var token = new StringBuilder();

        foreach (var c in candidate)
        {
            if (char.IsLetterOrDigit(c))
            {
                token.Append(c);
                continue;
            }

            AppendToken();
        }

        AppendToken();

        if (builder.Length == 0)
        {
            return fallback;
        }

        if (char.IsDigit(builder[0]))
        {
            builder.Insert(0, '_');
        }

        return builder.ToString();

        void AppendToken()
        {
            if (token.Length == 0)
            {
                return;
            }

            var text = token.ToString();
            token.Clear();

            var allUpper = true;
            foreach (var c in text)
            {
                if (char.IsLower(c))
                {
                    allUpper = false;
                    break;
                }
            }

            builder.Append(char.ToUpperInvariant(text[0]));
            builder.Append(allUpper ? text[1..].ToLowerInvariant() : text[1..]);
        }
    }

    /// <summary>
    /// The singular of a collection member name, so <c>orders</c> gives an <c>Order</c> record rather
    /// than an <c>Orders</c> one. Deliberately small and English-only; a wrong guess costs a rename.
    /// </summary>
    internal static string Singularize(string name)
    {
        if (name.Length < 3 || !name.EndsWith('s') || name.EndsWith("ss", StringComparison.Ordinal))
        {
            return name;
        }

        if (name.EndsWith("ies", StringComparison.Ordinal))
        {
            return name[..^3] + "y";
        }

        if (name.EndsWith("ses", StringComparison.Ordinal) ||
            name.EndsWith("xes", StringComparison.Ordinal) ||
            name.EndsWith("zes", StringComparison.Ordinal) ||
            name.EndsWith("ches", StringComparison.Ordinal) ||
            name.EndsWith("shes", StringComparison.Ordinal))
        {
            return name[..^2];
        }

        return name[..^1];
    }

    /// <summary>What one member's samples turned out to be, as a C# type.</summary>
    private sealed class Shape
    {
        public bool SawObject;
        public bool SawArray;
        public bool SawString;
        public bool SawNumber;
        public bool SawBoolean;
        public bool SawNull;
        public bool SawFractional;
        public bool SawLongNumber;
        public bool SawWideNumber;
        public int StringSamples;
        public int GuidStrings;
        public int DateStrings;
        public int ObjectSamples;
        public Shape? Element;
        public Dictionary<string, Member> Members { get; } = new(StringComparer.Ordinal);
        public List<string> MemberOrder { get; } = [];

        /// <summary>How many of the JSON kinds this member was ever seen as, ignoring nulls.</summary>
        public int Categories =>
            (SawObject ? 1 : 0) + (SawArray ? 1 : 0) + (SawString ? 1 : 0) +
            (SawNumber ? 1 : 0) + (SawBoolean ? 1 : 0);
    }

    private sealed class Member
    {
        public Shape Shape { get; } = new();

        public int SeenIn { get; set; }
    }

    /// <summary>Walks the learned shapes and writes the records, parents before children.</summary>
    private sealed class Emitter
    {
        private readonly StringBuilder _output = new();
        private readonly HashSet<string> _taken = new(StringComparer.Ordinal);
        private readonly Queue<(Shape Shape, string Name)> _queue = new();

        public void Emit(Shape root, string rootName)
        {
            _taken.Add(rootName);
            _queue.Enqueue((root, rootName));

            var first = true;
            while (_queue.Count > 0)
            {
                var (shape, name) = _queue.Dequeue();
                if (!first)
                {
                    _output.Append('\n');
                }

                first = false;
                Write(shape, name);
            }
        }

        public override string ToString() => _output.ToString();

        private void Write(Shape shape, string name)
        {
            _output.Append("public sealed record ").Append(name).Append('(');

            if (shape.MemberOrder.Count == 0)
            {
                _output.Append(");\n");
                return;
            }

            _output.Append('\n');

            // Two JSON member names can land on one C# identifier - `first-name` and `first_name` both
            // give FirstName, and `a` and `A` both give A - and a record with two parameters of the same
            // name does not compile. The names are claimed one at a time, per record, the same way nested
            // record names are claimed across the file.
            var claimed = new HashSet<string>(StringComparer.Ordinal) { name };

            for (var i = 0; i < shape.MemberOrder.Count; i++)
            {
                var key = shape.MemberOrder[i];
                var member = shape.Members[key];
                var propertyName = PropertyName(key, name, claimed);
                var optional = member.SeenIn < shape.ObjectSamples;
                var type = TypeOf(member.Shape, key, optional);

                _output.Append("    ").Append(type).Append(' ').Append(propertyName);
                _output.Append(i == shape.MemberOrder.Count - 1 ? ");\n" : ",\n");
            }
        }

        private static string PropertyName(string key, string recordName, HashSet<string> claimed)
        {
            var name = Identifier(key, "Value");
            if (string.Equals(name, recordName, StringComparison.Ordinal))
            {
                // A record parameter may not be spelled the same as its record (CS0542).
                name += "Value";
            }

            var candidate = name;
            var suffix = 2;
            while (!claimed.Add(candidate))
            {
                candidate = name + suffix.ToString(CultureInfo.InvariantCulture);
                suffix++;
            }

            return candidate;
        }

        private string TypeOf(Shape shape, string key, bool optional)
        {
            var nullable = optional || shape.SawNull;

            if (shape.Categories > 1)
            {
                return nullable ? "JsonElement?" : "JsonElement";
            }

            if (shape.SawObject)
            {
                return Reserve(shape, Identifier(Singularize(key), "Value")) + (nullable ? "?" : string.Empty);
            }

            if (shape.SawArray)
            {
                var element = shape.Element ?? new Shape();
                var elementType = element.Categories == 0
                    ? "object"
                    : TypeOf(element, Singularize(key), optional: false);
                return "List<" + elementType + ">" + (nullable ? "?" : string.Empty);
            }

            if (shape.SawString)
            {
                if (shape.StringSamples > 0 && shape.GuidStrings == shape.StringSamples)
                {
                    return nullable ? "Guid?" : "Guid";
                }

                if (shape.StringSamples > 0 && shape.DateStrings == shape.StringSamples)
                {
                    return nullable ? "DateTimeOffset?" : "DateTimeOffset";
                }

                return nullable ? "string?" : "string";
            }

            if (shape.SawNumber)
            {
                var numeric = shape.SawWideNumber ? "double"
                    : shape.SawFractional ? "decimal"
                    : shape.SawLongNumber ? "long"
                    : "int";
                return nullable ? numeric + "?" : numeric;
            }

            if (shape.SawBoolean)
            {
                return nullable ? "bool?" : "bool";
            }

            return "object?";
        }

        /// <summary>Claims a record name for a nested shape, disambiguating a collision by number.</summary>
        private string Reserve(Shape shape, string preferred)
        {
            var name = preferred;
            var suffix = 2;
            while (!_taken.Add(name))
            {
                name = preferred + suffix.ToString(CultureInfo.InvariantCulture);
                suffix++;
            }

            _queue.Enqueue((shape, name));
            return name;
        }
    }
}
