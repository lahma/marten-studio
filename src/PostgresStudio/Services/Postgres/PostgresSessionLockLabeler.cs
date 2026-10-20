using PostgresStudio.Services.Database;
using PostgresStudio.Services.Sources;

namespace PostgresStudio.Services.Postgres;

/// <summary>
/// PostgresStudio's <see cref="ISessionLockLabeler" />: a plain data source takes no lock of its own, so only the
/// relations are named, through the classifier the caller hands in.
/// </summary>
/// <remarks>A minimal body by PS-3a; PS-3b owns the implementation.</remarks>
internal sealed class PostgresSessionLockLabeler : ISessionLockLabeler
{
    /// <inheritdoc />
    public SessionLockLabels For(ResolvedSource source, DatabaseObjectClassifier? classifier)
    {
        ArgumentNullException.ThrowIfNull(source);
        return SessionLockLabels.RelationsOnly(source.Source.Key, classifier);
    }
}
