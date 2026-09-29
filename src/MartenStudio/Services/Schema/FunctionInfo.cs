using MartenStudio.Services.Database;

namespace MartenStudio.Services.Schema;

/// <summary>
/// One routine in the store's schemas - a function, procedure, aggregate or window function - and whether
/// this visitor may read its body.
/// </summary>
/// <remarks>
/// The body itself is not here. It is read one routine at a time, when a person opens the row, through
/// <see cref="IDatabaseObjectService.GetDefinitionAsync" /> with <see cref="Ref" /> - the database
/// browser's definition read, which is where the gate is enforced. <see cref="DefinitionAvailable" /> is the
/// same gate's answer asked in advance, so the tab never offers a body the service would refuse.
/// </remarks>
/// <param name="Schema">The schema it lives in.</param>
/// <param name="Name">The routine name.</param>
/// <param name="IdentityArguments">What tells one overload from another.</param>
/// <param name="Kind">Function, procedure, aggregate or window function.</param>
/// <param name="DeclaredByMarten">
/// Whether the store's own configuration asks for a function of this name. Matched against
/// <c>SchemaDeclarationReader</c> - the event store's own feature objects plus a list of the helper
/// functions Marten installs into the document schema - and never against
/// <c>IMartenDatabase.AllObjects()</c>, which applies migrations on the way. Anything else in the schema
/// is the application's own, and it is worth knowing which is which before running a migration.
/// </param>
/// <param name="Ownership">
/// Whose it is, by the database browser's rules: anything <c>mt_</c>-prefixed or declared is Marten's, and
/// the rest is somebody else's, with the "recognised by name" hint.
/// </param>
/// <param name="DefinitionAvailable">
/// Whether this visitor may read its body: Marten's own in the store's own schemas always; anything else
/// only past <c>Capabilities.BrowseDatabase</c>, the write policy and <c>BrowsableSchemas</c>; an
/// aggregate never, because Postgres prints no body for one.
/// </param>
/// <param name="DefinitionRefusal">Why not, naming what would change it - or <see langword="null" />.</param>
internal sealed record FunctionInfo(
    string Schema,
    string Name,
    string IdentityArguments,
    DatabaseObjectKind Kind,
    bool DeclaredByMarten,
    DatabaseObjectOwnership Ownership,
    bool DefinitionAvailable,
    string? DefinitionRefusal)
{
    /// <summary>The qualified name.</summary>
    public string QualifiedName => Schema + "." + Name;

    /// <summary>The name with its argument types, which is what makes an overload identifiable.</summary>
    public string Signature => Name + "(" + IdentityArguments + ")";

    /// <summary>What the definition read is asked with.</summary>
    public DatabaseObjectRef Ref => new(Kind, Schema, Name, IdentityArguments);

    /// <summary>A key unique to this overload, for <c>@key</c> and for remembering its body.</summary>
    public string Key => Schema + "." + Signature;
}

/// <summary>The Functions tab's answer for one database.</summary>
/// <param name="Functions">Every function, procedure, aggregate and window function in the store's schemas.</param>
/// <param name="Reason">Why nothing could be read, or <see langword="null" />.</param>
internal sealed record SchemaFunctions(IReadOnlyList<FunctionInfo> Functions, string? Reason)
{
    /// <summary>Nothing read yet.</summary>
    public static SchemaFunctions Empty { get; } = new([], null);

    /// <summary>The read failed, and this is why.</summary>
    public static SchemaFunctions Unavailable(string reason) => new([], reason);
}

/// <summary>The whole creation script for the store's schema objects.</summary>
/// <param name="Text">What <c>IDatabase.ToDatabaseScript()</c> returns.</param>
/// <param name="Reason">Why it could not be produced, or <see langword="null" />.</param>
internal sealed record DdlScript(string Text, string? Reason)
{
    /// <summary>Nothing read yet.</summary>
    public static DdlScript Empty { get; } = new(string.Empty, null);

    /// <summary>The script could not be produced, and this is why.</summary>
    public static DdlScript Unavailable(string reason) => new(string.Empty, reason);

    /// <summary>How large the script is, for the download button's label.</summary>
    public int Bytes => System.Text.Encoding.UTF8.GetByteCount(Text);

    /// <summary>Whether there is anything to copy or download.</summary>
    public bool HasText => Text.Trim().Length > 0;
}
