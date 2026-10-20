using System.Globalization;
using System.Text;

namespace PostgresStudio.Services.Database;

/// <summary>The three value types pgvector adds to Postgres.</summary>
internal enum PgVectorKind
{
    /// <summary><c>vector</c>: single-precision components, at most 16 000 of them.</summary>
    Vector,

    /// <summary><c>halfvec</c>: half-precision components, at most 16 000 of them.</summary>
    HalfVector,

    /// <summary><c>sparsevec</c>: the non-zero components of a vector, by index, and its dimensions.</summary>
    SparseVector,
}

/// <summary>A pgvector column's type, as the catalog declares it - never as a value says.</summary>
/// <param name="Kind">Which of the three types.</param>
/// <param name="Dimensions">
/// The dimensions its type modifier declares (<c>vector(1536)</c>), or <see langword="null" /> for a column declared
/// without one, which holds vectors of any length.
/// </param>
internal sealed record PgVectorColumn(PgVectorKind Kind, int? Dimensions)
{
    /// <summary>The type's name: <c>vector</c>, <c>halfvec</c> or <c>sparsevec</c>.</summary>
    public string TypeName => PgVector.TypeName(Kind);

    /// <summary>The type as Postgres prints it where its schema is on the search path: <c>vector(1536)</c>.</summary>
    public string Spelling => Dimensions is { } dimensions
        ? TypeName + "(" + dimensions.ToString(CultureInfo.InvariantCulture) + ")"
        : TypeName;

    /// <summary>What the type is, in words, for a badge's title.</summary>
    public string Description =>
        "A pgvector " + Kind switch
        {
            PgVectorKind.HalfVector => "half-precision vector",
            PgVectorKind.SparseVector => "sparse vector",
            _ => "vector",
        }
        + (Dimensions is { } dimensions
            ? " of " + dimensions.ToString("N0", CultureInfo.InvariantCulture) + " dimensions"
            : " of any length - the column declares no dimensions")
        + ", read from the column's type modifier: no value is read to learn it.";
}

/// <summary>What an operator class makes an index serve: the distance, and the operator a query orders by.</summary>
/// <param name="Name">The distance, in words: <c>cosine distance</c>.</param>
/// <param name="Operator">The operator a query has to order by for the index to be used: <c>&lt;=&gt;</c>.</param>
internal sealed record PgVectorDistance(string Name, string Operator);

/// <summary>One key column of an HNSW or IVFFlat index.</summary>
/// <param name="Definition">The column, or the expression, as <c>pg_get_indexdef(oid, k, true)</c> prints it - masked.</param>
/// <param name="OperatorClass">Its operator class: <c>vector_cosine_ops</c>, <c>halfvec_l2_ops</c>, <c>bit_hamming_ops</c>.</param>
/// <param name="Distance">What the operator class serves, or <see langword="null" /> for one the studio does not know.</param>
internal sealed record PgVectorIndexColumn(string Definition, string OperatorClass, PgVectorDistance? Distance);

/// <summary>One build parameter of an HNSW or IVFFlat index.</summary>
/// <param name="Name">The parameter: <c>m</c>, <c>ef_construction</c>, <c>lists</c>.</param>
/// <param name="Value">What the index was built with - the default when the index sets none.</param>
/// <param name="IsDefault">Whether the index sets none, so <paramref name="Value" /> is pgvector's default.</param>
/// <param name="Meaning">What it does, in a sentence; empty for a parameter the studio does not know.</param>
internal sealed record PgVectorIndexParameter(string Name, string Value, bool IsDefault, string Meaning);

/// <summary>An HNSW or IVFFlat index, as pgvector builds it.</summary>
/// <param name="AccessMethod"><c>hnsw</c> or <c>ivfflat</c>.</param>
/// <param name="Columns">Its key columns, each with its operator class.</param>
/// <param name="Parameters">Its build parameters, set or defaulted, in pgvector's order, then anything else it carries.</param>
internal sealed record PgVectorIndex(
    string AccessMethod,
    IReadOnlyList<PgVectorIndexColumn> Columns,
    IReadOnlyList<PgVectorIndexParameter> Parameters)
{
    /// <summary>The access method as pgvector's documentation spells it: <c>HNSW</c>, <c>IVFFlat</c>.</summary>
    public string Label => AccessMethod == PgVector.Hnsw ? "HNSW" : "IVFFlat";
}

/// <summary>The start of a pgvector value's text, ready to be shown collapsed.</summary>
/// <param name="Text">
/// The first few components as a person reads them - <c>[0.0123, -0.0441, 0.2, …]</c>, or
/// <c>{1:0.5, 7:0.25, …}/1000</c> for a sparse vector - ending in <c>…</c> whenever there is more.
/// </param>
/// <param name="Shown">How many components (a sparse vector's non-zero entries) <paramref name="Text" /> shows.</param>
/// <param name="Total">
/// How many there are, when the whole value was read; <see langword="null" /> when it was cut, because a cut value
/// is never counted.
/// </param>
internal sealed record PgVectorPreview(string Text, int Shown, int? Total)
{
    /// <summary>Whether <see cref="Text" /> shows every component.</summary>
    public bool Complete => Total is { } total && total == Shown;
}

/// <summary>
/// What the studio knows about pgvector: its column types, its two index access methods and their parameters, and
/// how one of its values is shown collapsed.
/// </summary>
/// <remarks>
/// <para>
/// <b>Everything here is read from <c>pg_catalog</c>, never from a value.</b> A column is a pgvector column when its
/// type - or its domain's base type - is one of the three types the <c>vector</c> extension owns
/// (<c>pg_depend.deptype 'e'</c>), so a host's own type that happens to be called <c>vector</c> is not one; its
/// dimensions are its type modifier, which <c>format_type</c> prints as <c>vector(1536)</c> and which is
/// <c>-1</c> for a column declared without any. An index is pgvector's when its access method is <c>hnsw</c> or
/// <c>ivfflat</c> and the same extension owns it.
/// </para>
/// <para>
/// <b>The defaults are pgvector's own</b>, read at pgvector's <c>v0.8.1</c> tag - the version the live tests run:
/// <c>m</c> 16 and <c>ef_construction</c> 64 for HNSW are <c>HNSW_DEFAULT_M</c> and
/// <c>HNSW_DEFAULT_EF_CONSTRUCTION</c> in <c>src/hnsw.h</c>, which its README documents too; <c>lists</c> 100 for
/// IVFFlat is <c>IVFFLAT_DEFAULT_LISTS</c> in <c>src/ivfflat.h</c> alone - the README gives no default, only its
/// advice of rows / 1000. Marten.PgVector's documentation says the same of its <c>VectorIndex&lt;T&gt;()</c> ("Omit
/// <c>m</c> and <c>efConstruction</c> to use pgvector's defaults (16 and 64)", Marten 9.39.0). A parameter an index does
/// not set is not in <c>pg_class.reloptions</c> at all, which is the only way the studio can tell that the default
/// applies.
/// </para>
/// <para>
/// <b>Where Marten meets it.</b> Marten 9 has no pgvector support of its own. The separate <c>Marten.PgVector</c>
/// package - unchanged between 9.39.0 and 9.50.2 - stores an embedding as a <c>float[]</c> inside the document's
/// JSON, declares an HNSW expression index over <c>((data -&gt;&gt; 'Embedding')::vector(n))</c> on the document
/// table, and builds a <c>VectorProjection</c> table with an <c>embedding vector(n)</c> column that a host registers
/// in <c>ExtendedSchemaObjects</c>. <c>halfvec</c>, <c>sparsevec</c>, IVFFlat and an index on a plain column come
/// only from a host's own DDL.
/// </para>
/// </remarks>
internal static class PgVector
{
    /// <summary>The extension's name in <c>pg_extension</c>: <c>vector</c>, whatever schema it was created in.</summary>
    public const string Extension = "vector";

    /// <summary>pgvector's graph index.</summary>
    public const string Hnsw = "hnsw";

    /// <summary>pgvector's inverted-list index.</summary>
    public const string IvfFlat = "ivfflat";

    /// <summary>HNSW's <c>m</c> when an index sets none.</summary>
    public const int HnswDefaultM = 16;

    /// <summary>HNSW's <c>ef_construction</c> when an index sets none.</summary>
    public const int HnswDefaultEfConstruction = 64;

    /// <summary>IVFFlat's <c>lists</c> when an index sets none.</summary>
    public const int IvfFlatDefaultLists = 100;

    /// <summary>Where the defaults come from, as the screens cite it.</summary>
    public const string DefaultsSource = "pgvector 0.8.1";

    /// <summary>How many components a collapsed value shows before its <c>…</c>.</summary>
    public const int PreviewComponents = 3;

    /// <summary>The longest a single component may run in a preview before it is not a number pgvector printed.</summary>
    private const int MaxComponentLength = 48;

    /// <summary>The three type names, as <c>pg_type.typname</c> holds them.</summary>
    public static IReadOnlyList<string> TypeNames { get; } = ["vector", "halfvec", "sparsevec"];

    /// <summary>The type name of <paramref name="kind" />.</summary>
    public static string TypeName(PgVectorKind kind) => kind switch
    {
        PgVectorKind.HalfVector => "halfvec",
        PgVectorKind.SparseVector => "sparsevec",
        _ => "vector",
    };

    /// <summary>
    /// The pgvector type of a column, or <see langword="null" /> when it is not one.
    /// </summary>
    /// <param name="extension">The extension that owns the column's type (or its domain's base type), from <c>pg_depend</c>.</param>
    /// <param name="typeName">That type's <c>typname</c>.</param>
    /// <param name="typeModifier">The column's <c>atttypmod</c> - a domain's <c>typtypmod</c> - which is pgvector's dimensions, or <c>-1</c>.</param>
    public static PgVectorColumn? Column(string? extension, string? typeName, int typeModifier)
    {
        if (!string.Equals(extension, Extension, StringComparison.Ordinal))
        {
            return null;
        }

        PgVectorKind? kind = typeName switch
        {
            "vector" => PgVectorKind.Vector,
            "halfvec" => PgVectorKind.HalfVector,
            "sparsevec" => PgVectorKind.SparseVector,
            _ => null,
        };

        return kind is { } found ? new PgVectorColumn(found, typeModifier > 0 ? typeModifier : null) : null;
    }

    /// <summary>Whether an access method is one of pgvector's two by name: <c>hnsw</c> or <c>ivfflat</c>.</summary>
    public static bool IsVectorAccessMethod(string? accessMethod) =>
        string.Equals(accessMethod, Hnsw, StringComparison.Ordinal) || string.Equals(accessMethod, IvfFlat, StringComparison.Ordinal);

    /// <summary>
    /// An HNSW or IVFFlat index described, or <see langword="null" /> for any other index - including an
    /// <c>hnsw</c> access method some other extension owns, whose parameters and defaults are not pgvector's.
    /// </summary>
    /// <param name="ownedByPgVector">Whether the <c>vector</c> extension owns the index's access method.</param>
    /// <param name="accessMethod"><c>pg_am.amname</c>.</param>
    /// <param name="keyColumns">Each key column as <c>pg_get_indexdef(oid, k, true)</c> prints it, already masked.</param>
    /// <param name="operatorClasses">Each key column's operator class, <c>pg_opclass.opcname</c>, in key order.</param>
    /// <param name="reloptions">The index's <c>pg_class.reloptions</c>, as <c>name=value</c>.</param>
    public static PgVectorIndex? Index(
        bool ownedByPgVector,
        string? accessMethod,
        IReadOnlyList<string?>? keyColumns,
        IReadOnlyList<string?>? operatorClasses,
        IReadOnlyList<string>? reloptions)
    {
        if (!ownedByPgVector || !IsVectorAccessMethod(accessMethod))
        {
            return null;
        }

        List<PgVectorIndexColumn> columns = [];
        int count = Math.Max(keyColumns?.Count ?? 0, operatorClasses?.Count ?? 0);

        for (int i = 0; i < count; i++)
        {
            string definition = keyColumns is not null && i < keyColumns.Count ? keyColumns[i] ?? string.Empty : string.Empty;
            string operatorClass = operatorClasses is not null && i < operatorClasses.Count ? operatorClasses[i] ?? string.Empty : string.Empty;

            columns.Add(new PgVectorIndexColumn(definition, operatorClass, DistanceOf(operatorClass)));
        }

        return new PgVectorIndex(accessMethod!, columns, Parameters(accessMethod!, reloptions));
    }

    /// <summary>
    /// What an operator class serves, from the distance its name ends in - <c>vector_cosine_ops</c>,
    /// <c>halfvec_l2_ops</c>, <c>sparsevec_ip_ops</c>, <c>bit_hamming_ops</c> - or <see langword="null" />.
    /// </summary>
    /// <remarks>
    /// The operator is the one pgvector's documentation pairs with each: an index is used only by a query that
    /// orders by the operator its operator class was built for, and is otherwise silently a sequential scan.
    /// </remarks>
    public static PgVectorDistance? DistanceOf(string? operatorClass)
    {
        if (string.IsNullOrEmpty(operatorClass))
        {
            return null;
        }

        return operatorClass switch
        {
            _ when operatorClass.EndsWith("_l2_ops", StringComparison.Ordinal) => new PgVectorDistance("L2 distance", "<->"),
            _ when operatorClass.EndsWith("_ip_ops", StringComparison.Ordinal) => new PgVectorDistance("negative inner product", "<#>"),
            _ when operatorClass.EndsWith("_cosine_ops", StringComparison.Ordinal) => new PgVectorDistance("cosine distance", "<=>"),
            _ when operatorClass.EndsWith("_l1_ops", StringComparison.Ordinal) => new PgVectorDistance("L1 distance", "<+>"),
            _ when operatorClass.EndsWith("_hamming_ops", StringComparison.Ordinal) => new PgVectorDistance("Hamming distance", "<~>"),
            _ when operatorClass.EndsWith("_jaccard_ops", StringComparison.Ordinal) => new PgVectorDistance("Jaccard distance", "<%>"),
            _ => null,
        };
    }

    /// <summary>
    /// An index's build parameters: pgvector's own, in its order, each set or defaulted, then any other
    /// <c>reloptions</c> entry as it is.
    /// </summary>
    public static IReadOnlyList<PgVectorIndexParameter> Parameters(string accessMethod, IReadOnlyList<string>? reloptions)
    {
        List<(string Name, string Value)> set = [];

        foreach (string option in reloptions ?? [])
        {
            int equals = option.IndexOf('=', StringComparison.Ordinal);
            if (equals > 0)
            {
                set.Add((option[..equals].Trim().ToLowerInvariant(), option[(equals + 1)..].Trim()));
            }
        }

        (string Name, int Default, string Meaning)[] known = accessMethod == Hnsw
            ?
            [
                ("m", HnswDefaultM, "The most connections each node keeps in each layer of the graph: higher recalls better and takes more memory and build time."),
                ("ef_construction", HnswDefaultEfConstruction, "How many candidates the build keeps while it links each vector: higher builds slower and recalls better."),
            ]
            :
            [
                ("lists", IvfFlatDefaultLists, "How many lists the vectors are clustered into when the index is built; pgvector suggests rows / 1000 up to a million rows, and the square root of the rows beyond."),
            ];

        List<PgVectorIndexParameter> parameters = [];

        foreach ((string name, int fallback, string meaning) in known)
        {
            (string Name, string Value) found = set.FirstOrDefault(x => x.Name == name);

            parameters.Add(found.Name is null
                ? new PgVectorIndexParameter(name, fallback.ToString(CultureInfo.InvariantCulture), true, meaning)
                : new PgVectorIndexParameter(name, found.Value, false, meaning));
        }

        foreach ((string name, string value) in set)
        {
            if (!known.Any(x => x.Name == name))
            {
                parameters.Add(new PgVectorIndexParameter(name, value, false, string.Empty));
            }
        }

        return parameters;
    }

    /// <summary>
    /// The first <see cref="PreviewComponents" /> components of a value's text - pgvector prints a <c>vector</c> and a
    /// <c>halfvec</c> as <c>[1,2.5,-3]</c> and a <c>sparsevec</c> as <c>{1:1.5,3:2}/5</c> - or <see langword="null" />
    /// when the text is not one of those shapes.
    /// </summary>
    /// <param name="text">The value's text, as far as it was read.</param>
    /// <param name="cut">Whether the server cut it, so its end is not the value's end.</param>
    public static PgVectorPreview? Preview(string? text, bool cut)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        ReadOnlySpan<char> span = text.AsSpan().Trim();

        if (span.Length == 0 || span[0] is not ('[' or '{'))
        {
            return null;
        }

        bool sparse = span[0] == '{';
        char close = sparse ? '}' : ']';

        List<string> components = [];
        int total = 0;
        bool closed = false;
        int start = 1;
        string? suffix = null;

        for (int i = 1; i <= span.Length; i++)
        {
            if (i == span.Length)
            {
                // The text ran out before the closing bracket: only a cut value may do that, and its last
                // component may be half a number, so it is not counted.
                if (!cut)
                {
                    return null;
                }

                break;
            }

            char c = span[i];

            if (c != ',' && c != close)
            {
                continue;
            }

            ReadOnlySpan<char> component = span[start..i].Trim();

            if (component.Length > 0)
            {
                if (component.Length > MaxComponentLength || !IsComponent(component, sparse))
                {
                    return null;
                }

                if (components.Count < PreviewComponents)
                {
                    components.Add(component.ToString());
                }

                total++;
            }
            else if (c == ',' || total > 0)
            {
                // "[1,,2]" or "[1,]" is not pgvector's output.
                return null;
            }

            start = i + 1;

            if (c == close)
            {
                closed = true;
                ReadOnlySpan<char> rest = span[(i + 1)..].Trim();

                if (cut)
                {
                    // What follows the bracket of a value the server cut is not the value's end: a sparse vector's
                    // "/1000" may have been cut to "/10", and the reader marks the cut with an ellipsis. Neither is
                    // read, and nothing about the value is counted.
                    break;
                }

                if (sparse)
                {
                    if (rest.Length < 2 || rest[0] != '/' || !int.TryParse(rest[1..], NumberStyles.None, CultureInfo.InvariantCulture, out int dimensions))
                    {
                        return null;
                    }

                    suffix = "/" + dimensions.ToString(CultureInfo.InvariantCulture);
                }
                else if (rest.Length > 0)
                {
                    return null;
                }

                break;
            }
        }

        bool more = cut || !closed || total > components.Count;

        var builder = new StringBuilder();
        builder.Append(sparse ? '{' : '[');

        for (int i = 0; i < components.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(", ");
            }

            builder.Append(sparse ? components[i].Replace(" ", string.Empty, StringComparison.Ordinal) : components[i]);
        }

        if (more)
        {
            builder.Append(components.Count > 0 ? ", …" : "…");
        }

        builder.Append(sparse ? '}' : ']');
        builder.Append(suffix);

        return new PgVectorPreview(builder.ToString(), components.Count, closed && !cut ? total : null);
    }

    /// <summary>
    /// A value shown collapsed, as its type and the start of its components: <c>vector(1536) [0.0123, -0.0441, 0.2, …]</c>.
    /// </summary>
    public static string Collapsed(PgVectorColumn column, PgVectorPreview preview)
    {
        ArgumentNullException.ThrowIfNull(column);
        ArgumentNullException.ThrowIfNull(preview);

        return column.Spelling + " " + preview.Text;
    }

    /// <summary>What a collapsed value is, in words, for its title and its screen-reader name.</summary>
    public static string Describe(PgVectorColumn column, PgVectorPreview preview)
    {
        ArgumentNullException.ThrowIfNull(column);
        ArgumentNullException.ThrowIfNull(preview);

        bool one = preview.Total == 1;
        string noun = column.Kind == PgVectorKind.SparseVector
            ? one ? "non-zero entry" : "non-zero entries"
            : one ? "component" : "components";
        string count = preview.Total is { } total
            ? total.ToString("N0", CultureInfo.InvariantCulture) + " " + noun
            : column.Dimensions is { } dimensions
                ? dimensions.ToString("N0", CultureInfo.InvariantCulture) + " dimensions"
                : "more " + noun + " than were read";

        return column.Spelling + ": " + count + ", the first " +
               preview.Shown.ToString(CultureInfo.InvariantCulture) + " shown.";
    }

    private static bool IsComponent(ReadOnlySpan<char> component, bool sparse)
    {
        if (!sparse)
        {
            return IsNumber(component);
        }

        int colon = component.IndexOf(':');

        return colon > 0
               && int.TryParse(component[..colon].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out _)
               && IsNumber(component[(colon + 1)..].Trim());
    }

    private static bool IsNumber(ReadOnlySpan<char> value) =>
        value.Length > 0
        && (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _)
            || value is "NaN" or "Infinity" or "-Infinity");
}
