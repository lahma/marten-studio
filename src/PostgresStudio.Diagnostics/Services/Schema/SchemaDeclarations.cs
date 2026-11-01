namespace PostgresStudio.Services.Schema;

/// <summary>One index the store's configuration asks for, flattened out of <c>IDocumentType.Indexes</c>.</summary>
/// <param name="CollectionAlias">The document type that declares it.</param>
/// <param name="DocumentTypeName">The .NET type name.</param>
/// <param name="Schema">The schema its table lives in.</param>
/// <param name="Table">The table it is on.</param>
/// <param name="Name">The index name.</param>
/// <param name="Definition">The statement Weasel would write for it.</param>
internal sealed record DeclaredIndex(
    string CollectionAlias,
    string DocumentTypeName,
    string Schema,
    string Table,
    string Name,
    string Definition);

/// <summary>
/// Everything the Schema screen's read paths need to know about what <c>StoreOptions</c> declares,
/// derived from the options alone - the model the database browser classifies objects with, read by
/// MartenStudio's <c>SchemaDeclarationReader</c>.
/// </summary>
/// <param name="Schemas">
/// The schemas this store owns in this database, in a stable order: the document and event schemas, every
/// document type's, and every event-store feature object's (a projection's table included). Not the schema of
/// an <c>ExtendedSchemaObjects</c> object, on purpose: this is D27's "store's own schemas", whose structure the
/// database browser shows without <c>BrowseDatabase</c> and which the Schema screen lists, and handing an object
/// to Marten to manage does not hand its schema to every visitor. Code that needs such an object's schema reads
/// it from <see cref="MartenDeclaredObject.Schema" />, never by splitting a key against this list.
/// </param>
/// <param name="Indexes">Every index the configuration asks for, on a table it manages.</param>
/// <param name="ManagedTables">
/// The qualified names of the tables an apply from this store migrates: the document and event tables,
/// the projections' tables, and every <c>StoreOptions.Storage.ExtendedSchemaObjects</c> table. An index on
/// any other table in these schemas is not one the studio can see an apply dropping - which is not a
/// promise that none will: a feature added with <c>StoreOptions.Storage.Add(...)</c> is migrated too, and
/// cannot be listed without applying migrations (AGENTS.md hard rule 14).
/// </param>
/// <param name="IgnoredIndexes">
/// The qualified index names the host told Marten's migration detection to leave alone
/// (<c>Schema.For&lt;T&gt;().IgnoreIndex(name)</c> / <c>Events.IgnoreIndex(name)</c>). Weasel removes
/// these from both sides of the delta, so they are neither created nor dropped.
/// </param>
/// <param name="Functions">The qualified names of the functions Marten installs.</param>
internal sealed record SchemaDeclarations(
    IReadOnlyList<string> Schemas,
    IReadOnlyList<DeclaredIndex> Indexes,
    IReadOnlySet<string> ManagedTables,
    IReadOnlySet<string> IgnoredIndexes,
    IReadOnlySet<string> Functions)
{
    /// <summary>
    /// Every object the configuration declares, keyed by <see cref="SchemaKey.For(string, string)" />, with
    /// what Marten means it to be.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A wider net than <see cref="ManagedTables" />: it also holds the event store's sequences (which the
    /// Schema screen has never listed) and <c>StoreOptions.Storage.ExtendedSchemaObjects</c> (EF Core
    /// projection tables, PgVector, anything a host hands Marten to manage), and it says <em>what</em> each
    /// one is rather than only that Marten manages it. The database browser classifies objects with it, and
    /// so do the Schema screen's Tables and Indexes tabs (DB-7): adding to it changes what they show.
    /// </para>
    /// <para>
    /// Case-insensitive, like every other set on this record. Erring towards "this is Marten's" is the
    /// safe direction for a map whose job is to keep Marten's rows from being read raw.
    /// </para>
    /// <para>
    /// <b>Not everything Marten creates can be here.</b> Features added with
    /// <c>StoreOptions.Storage.Add(IFeatureSchema)</c> - TimescaleDB's hypertable and continuous-aggregate
    /// objects, a host's own feature - are reachable only through Marten's internal
    /// <c>StorageFeatures.AllActiveFeatures(database)</c>, which applies migrations on the way (AGENTS.md hard
    /// rule 14), and the HiLo <c>SequenceFactory</c> is internal. Those objects are known here only when
    /// their name starts with <c>mt_</c>, which is the database browser's own rule for "Marten
    /// infrastructure" in any schema; anything else they create reads as the host's own.
    /// </para>
    /// </remarks>
    public IReadOnlyDictionary<string, MartenDeclaredObject> Objects { get; init; } =
        new Dictionary<string, MartenDeclaredObject>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Why <see cref="Objects" /> is incomplete, or <see langword="null" /> when it is not.
    /// </summary>
    /// <remarks>
    /// Recorded rather than thrown so that <c>SchemaDeclarationReader.Read</c>'s existing callers -
    /// the Schema screen, which reads <see cref="Schemas" />, <see cref="Indexes" /> and
    /// <see cref="Functions" /> - behave exactly as they did before the map existed.
    /// <c>SchemaDeclarationReader.ReadForClassification</c> turns it into a failure.
    /// </remarks>
    public string? ObjectsFailure { get; init; }
}

/// <summary>What Marten declares one database object to be.</summary>
internal enum MartenObjectKind
{
    /// <summary>A document type's table. See <see cref="MartenDeclaredObject.Visible" />.</summary>
    DocumentTable,

    /// <summary>One of the event store's own tables: <c>mt_events</c>, <c>mt_streams</c> and their kin.</summary>
    EventTable,

    /// <summary>
    /// A relational object Marten manages on the host's behalf and whose rows are the host's own data: a
    /// flat-table projection's table, an <c>EventProjection.SchemaObjects</c> table, anything in
    /// <c>ExtendedSchemaObjects</c> that is not a function or a sequence.
    /// </summary>
    ProjectionOrExtendedTable,

    /// <summary>Marten's own bookkeeping table (<c>mt_hilo</c>).</summary>
    Infrastructure,

    /// <summary>A function Marten installs or was handed to manage.</summary>
    Function,

    /// <summary>A sequence Marten creates or was handed to manage.</summary>
    Sequence,
}

/// <summary>One object a store's configuration declares, by what it is.</summary>
/// <param name="Schema">
/// The schema it is declared in. Carried rather than split back out of its key: an
/// <c>ExtendedSchemaObjects</c> table may sit in a schema of its own that is deliberately <em>not</em> in
/// <see cref="SchemaDeclarations.Schemas" /> (D27's "store's own schemas"), and a key is <c>schema.name</c>
/// with a name that may itself hold a dot.
/// </param>
/// <param name="Name">The object's own name, unqualified.</param>
/// <param name="Kind">What Marten means it to be.</param>
/// <param name="Alias">
/// The collection alias of a document table whose type is visible; <see langword="null" /> for everything
/// else, including a hidden type's table - its alias is exactly what a hidden type must not show.
/// </param>
/// <param name="Visible">
/// <see langword="false" /> only for a document table whose type
/// <c>MartenStudioOptions.IsDocumentTypeVisible</c> hides.
/// </param>
internal sealed record MartenDeclaredObject(string Schema, string Name, MartenObjectKind Kind, string? Alias = null, bool Visible = true)
{
    /// <summary>The key <see cref="SchemaDeclarations.Objects" /> holds it under.</summary>
    public string Key => SchemaKey.For(Schema, Name);
}

/// <summary>
/// What <c>SchemaDeclarationReader.ReadForClassification</c> answers: the declarations, or why
/// they could not be read - never an empty map standing in for a failure.
/// </summary>
internal sealed record SchemaDeclarationRead
{
    private SchemaDeclarationRead(SchemaDeclarations? declarations, string? failure)
    {
        Declarations = declarations;
        Failure = failure;
    }

    /// <summary>The declarations, when the read succeeded.</summary>
    public SchemaDeclarations? Declarations { get; }

    /// <summary>Why it did not, in the configuration's own words.</summary>
    public string? Failure { get; }

    /// <summary>Whether the declarations are complete.</summary>
    [System.Diagnostics.CodeAnalysis.MemberNotNullWhen(true, nameof(Declarations))]
    public bool Succeeded => Declarations is not null;

    /// <summary>A complete read.</summary>
    public static SchemaDeclarationRead Success(SchemaDeclarations declarations) =>
        new(declarations ?? throw new ArgumentNullException(nameof(declarations)), null);

    /// <summary>A read that failed, and why.</summary>
    public static SchemaDeclarationRead Failed(string reason) =>
        new(null, string.IsNullOrWhiteSpace(reason) ? "The store's configuration could not be read." : reason);
}

/// <summary>The qualified-name keys the Schema screen joins on.</summary>
/// <remarks>
/// One place, because a table key and an index key that disagreed about their separator would produce a
/// screen on which nothing is ever declared - which is exactly the failure that has to be loud.
/// </remarks>
internal static class SchemaKey
{
    /// <summary>A qualified table or function name.</summary>
    public static string For(string schema, string name) => schema + "." + name;

    /// <summary>A qualified index name.</summary>
    public static string For(string schema, string table, string name) => schema + "." + table + "." + name;
}

/// <summary>
/// A CLR type's name as a person would write it in C#.
/// </summary>
/// <remarks>
/// <c>Type.Name</c> renders a generic as <c>Envelope`1</c>, and a suggestion that reads
/// <c>opts.Schema.For&lt;Envelope`1&gt;()</c> is one nobody can paste. Nested types keep their outer
/// name, because <c>Outer.Inner</c> is what compiles.
/// </remarks>
internal static class SchemaTypeName
{
    /// <summary>The C#-friendly name of <paramref name="type" />.</summary>
    public static string Of(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);

        string name = type.Name;

        int tick = name.IndexOf('`', StringComparison.Ordinal);
        if (tick >= 0)
        {
            name = name[..tick];
        }

        if (type.IsGenericType)
        {
            Type[] arguments = type.GetGenericArguments();
            List<string> rendered = new(arguments.Length);
            foreach (Type argument in arguments)
            {
                rendered.Add(Of(argument));
            }

            name = name + "<" + string.Join(", ", rendered) + ">";
        }

        return type.DeclaringType is { } declaring && !type.IsGenericParameter
            ? Of(declaring) + "." + name
            : name;
    }
}
