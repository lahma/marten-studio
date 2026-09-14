namespace MartenStudio.Services.Schema;

/// <summary>
/// The SQL Marten would run to bring this database up to its configuration.
/// </summary>
/// <remarks>
/// Rendering a migration is <c>SchemaMigration.WriteAllUpdates(TextWriter, Migrator, AutoCreate)</c> into
/// a <see cref="StringWriter" /> and nothing else: Weasel 9.32's <c>SchemaMigration</c> has neither
/// <c>ToSql()</c> nor <c>UpdateSql()</c>, whatever the plan's Appendix A said. Producing this <em>never
/// applies anything</em> - it opens a connection to read the current shape of the database and writes the
/// difference to a string.
/// </remarks>
/// <param name="Sql">The migration script, exactly as it would be executed.</param>
/// <param name="ObjectCount">How many schema objects the script touches.</param>
/// <param name="Deltas">Which objects, and how each of them differs.</param>
/// <param name="Difference">Weasel's overall verdict for the migration.</param>
/// <param name="Notice">
/// What could not be rendered, or <see langword="null" />. A migration whose verdict is <c>Invalid</c>
/// cannot be written as an update script at all - Weasel refuses, because applying it would mean dropping
/// something - and saying so is more use than an empty code block.
/// </param>
internal sealed record MigrationPreview(
    string Sql,
    int ObjectCount,
    IReadOnlyList<SchemaObjectDifference> Deltas,
    string Difference,
    string? Notice)
{
    /// <summary>Nobody has asked for a preview yet.</summary>
    public static MigrationPreview None { get; } = new(string.Empty, 0, [], "None", null);

    /// <summary>Whether there is any SQL to copy, download or apply.</summary>
    public bool HasSql => Sql.Trim().Length > 0;

    /// <summary>How large the script is, for the download button's label.</summary>
    public int Bytes => System.Text.Encoding.UTF8.GetByteCount(Sql);
}

/// <summary>What applying a migration did.</summary>
/// <param name="Succeeded">Whether Marten applied the changes.</param>
/// <param name="Difference">Weasel's verdict for what it applied.</param>
/// <param name="ObjectCount">How many objects the applied script touched.</param>
/// <param name="Message">What to put on screen, whether it worked or not.</param>
/// <param name="SqlState">
/// The Postgres <c>SqlState</c> when the database refused, so the page can turn <c>42501</c> into a
/// sentence about privileges rather than showing a five-character code.
/// </param>
internal sealed record SchemaApplyResult(
    bool Succeeded,
    string Difference,
    int ObjectCount,
    string Message,
    string? SqlState)
{
    /// <summary>Whether the refusal was "this role may not do that".</summary>
    public bool IsInsufficientPrivilege =>
        string.Equals(SqlState, Npgsql.PostgresErrorCodes.InsufficientPrivilege, StringComparison.Ordinal);
}
