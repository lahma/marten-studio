using PostgresStudio.Services.Database;
using PostgresStudio.Services.Sources;

namespace PostgresStudio.Services.Postgres;

/// <summary>
/// Postgres Studio's <see cref="ISessionLockLabeler" />: a plain data source takes no advisory lock of its own, so only the
/// relations are labelled, through the classifier the caller hands in, and every advisory lock is the application's.
/// </summary>
/// <remarks>
/// <para>
/// <b>The words of a database, not a store.</b> An advisory lock is "an advisory lock the application took" - Npgsql
/// takes none of its own, and Postgres knows only the key, which is the application's to interpret - and a relation
/// nobody declares is "a relation", named only to a visitor who may see details, as the shared labels always do. An
/// object the <c>mt_</c> rule calls Marten's is "a relation named like Marten's", named to nobody: its name can carry a
/// Marten store's alias or tenant, and Marten Studio is where it is read.
/// </para>
/// <para>
/// Configuration only, never a connection: a singleton, cheap enough for a sweep and the Sessions poll, and it never
/// throws for a source - a label is a decoration.
/// </para>
/// </remarks>
internal sealed class PostgresSessionLockLabeler : ISessionLockLabeler
{
    /// <summary>The labels' words for a plain data source.</summary>
    internal static SessionLockWording Wording { get; } = new(
        OtherAdvisoryLock: "an advisory lock the application took",
        OtherRelation: "a relation",
        UndeclaredMartenRelation: "a relation named like Marten's");

    /// <inheritdoc />
    public SessionLockLabels For(ResolvedSource source, DatabaseObjectClassifier? classifier)
    {
        ArgumentNullException.ThrowIfNull(source);
        return SessionLockLabels.RelationsOnly(source.Source.Key, classifier, Wording);
    }
}
