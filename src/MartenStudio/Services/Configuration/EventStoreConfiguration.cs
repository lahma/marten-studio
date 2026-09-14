namespace MartenStudio.Services.Configuration;

/// <summary>The event-store card.</summary>
/// <param name="Values">Stream identity, append mode, tenancy, schema and the rest.</param>
/// <param name="Metadata">
/// The four flags on <c>Marten.Events.IReadonlyMetadataConfig</c> - and it is four, not five: the
/// interface carries <c>CausationIdEnabled</c>, <c>CorrelationIdEnabled</c>, <c>HeadersEnabled</c> and
/// <c>UserNameEnabled</c> and nothing else, which is why the rest of the <c>mt_events</c> column set has
/// to be discovered from <c>information_schema</c> rather than read out of configuration (plan U8/U14).
/// </param>
/// <param name="Daemon">The async daemon's settings, from <c>StoreOptions.Events.Daemon</c>.</param>
/// <param name="KnownEventTypeCount">How many event types the store knows about.</param>
/// <param name="ProjectionCount">How many projections and subscriptions are registered.</param>
internal sealed record EventStoreConfiguration(
    IReadOnlyList<ConfigurationValue> Values,
    IReadOnlyList<ConfigurationValue> Metadata,
    IReadOnlyList<ConfigurationValue> Daemon,
    int KnownEventTypeCount,
    int ProjectionCount);

/// <summary>One member Marten copies out of the document into a real column.</summary>
/// <param name="Member">The .NET member, as <c>DuplicatedField.MemberName</c> spells it.</param>
/// <param name="Column">The column it becomes.</param>
/// <param name="PgType">The Postgres type of that column.</param>
internal sealed record DuplicatedFieldConfiguration(string Member, string Column, string PgType);

/// <summary>One index the configuration asks for on a document type's table.</summary>
/// <param name="Name">The index name Marten gives it.</param>
/// <param name="Definition">The statement Weasel would write for it.</param>
internal sealed record ConfiguredIndex(string Name, string Definition);

/// <summary>One foreign key the configuration asks for.</summary>
/// <param name="Name">The constraint name.</param>
/// <param name="Columns">The columns it is on.</param>
/// <param name="LinkedTable">The table it points at.</param>
/// <param name="LinkedColumns">The columns it points at.</param>
/// <param name="OnDelete">What happens to this row when the referenced row goes.</param>
internal sealed record ConfiguredForeignKey(
    string Name,
    string Columns,
    string LinkedTable,
    string LinkedColumns,
    string OnDelete);

/// <summary>One subclass in a document hierarchy.</summary>
/// <param name="Alias">The alias stored in <c>mt_doc_type</c>.</param>
/// <param name="TypeName">The .NET type.</param>
internal sealed record ConfiguredSubClass(string Alias, string TypeName);

/// <summary>One document type's card.</summary>
/// <param name="Alias">Marten's alias for the type, which is also the table suffix.</param>
/// <param name="TypeName">The .NET type's short name.</param>
/// <param name="FullTypeName">The .NET type's full name, which is what goes in <c>mt_dotnet_type</c>.</param>
/// <param name="Table">The qualified table name.</param>
/// <param name="Values">Identity, concurrency, delete style, tenancy and the rest, each with its member.</param>
/// <param name="DuplicatedFields">Members copied into real columns.</param>
/// <param name="Indexes">Indexes the configuration asks for.</param>
/// <param name="ForeignKeys">Foreign keys the configuration asks for.</param>
/// <param name="SubClasses">The hierarchy, when there is one.</param>
/// <param name="MetadataColumns">The metadata columns that are enabled on this type.</param>
internal sealed record DocumentTypeConfiguration(
    string Alias,
    string TypeName,
    string FullTypeName,
    string Table,
    IReadOnlyList<ConfigurationValue> Values,
    IReadOnlyList<DuplicatedFieldConfiguration> DuplicatedFields,
    IReadOnlyList<ConfiguredIndex> Indexes,
    IReadOnlyList<ConfiguredForeignKey> ForeignKeys,
    IReadOnlyList<ConfiguredSubClass> SubClasses,
    IReadOnlyList<string> MetadataColumns)
{
    /// <summary>Whether the search text matches this card.</summary>
    public bool Matches(string needle) =>
        Alias.Contains(needle, StringComparison.OrdinalIgnoreCase)
        || TypeName.Contains(needle, StringComparison.OrdinalIgnoreCase)
        || Table.Contains(needle, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Everything the Configuration page renders for one store.</summary>
/// <param name="Store">The store card.</param>
/// <param name="Events">The event-store card.</param>
/// <param name="DocumentTypes">One card per registered document type.</param>
/// <param name="PostgresVersion">The server version, or <see langword="null" /> when it could not be read.</param>
internal sealed record StudioConfiguration(
    StoreConfiguration Store,
    EventStoreConfiguration Events,
    IReadOnlyList<DocumentTypeConfiguration> DocumentTypes,
    string? PostgresVersion);
