namespace MartenStudio.Services.Schema;

/// <summary>One function in the store's schemas.</summary>
/// <param name="Schema">The schema it lives in.</param>
/// <param name="Name">The function name.</param>
/// <param name="Signature">The name with its argument types, which is what makes an overload identifiable.</param>
/// <param name="Definition">What <c>pg_get_functiondef</c> reconstructs for it.</param>
/// <param name="DeclaredByMarten">
/// Whether the store's own configuration asks for a function of this name. Matched against
/// <c>SchemaDeclarationReader</c> - the event store's own feature objects plus a list of the helper
/// functions Marten installs into the document schema - and never against
/// <c>IMartenDatabase.AllObjects()</c>, which applies migrations on the way. Anything else in the schema
/// is the application's own, and it is worth knowing which is which before running a migration.
/// </param>
internal sealed record FunctionInfo(
    string Schema,
    string Name,
    string Signature,
    string Definition,
    bool DeclaredByMarten)
{
    /// <summary>The qualified name.</summary>
    public string QualifiedName => Schema + "." + Name;
}

/// <summary>The Functions tab's answer for one database.</summary>
/// <param name="Functions">Every ordinary function in the store's schemas.</param>
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
