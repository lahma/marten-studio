using PostgresStudio.Services.Database;

namespace PostgresStudio.Services.Sources;

/// <summary>
/// Names the locks a source's own machinery takes, so that Database › Sessions and the lock checks (C04, C06, T14) can
/// say "the async daemon's leadership lock" rather than an advisory key.
/// </summary>
/// <remarks>
/// <para>
/// <b>What it is for.</b> A lock in <c>pg_locks</c> is a number or a relation. A source that takes advisory locks of its
/// own - Marten's daemon leadership and shard keys, its schema-apply lock - knows which numbers are its; the relations
/// are labelled through the classifier the database browser already built (<see cref="DatabaseObjectClassifier" />),
/// whose ownership a label repeats. A plain data source takes no lock of its own and answers labels that name only the
/// relations.
/// </para>
/// <para>
/// <b>What it must not do.</b> It reads configuration only: no connection, no query, no daemon lookup that could start
/// anything. It is called inside a health sweep and on the Sessions page's poll, so it must be cheap, and it must never
/// throw for a source it cannot read - a label is a decoration, and the answer then names nothing.
/// </para>
/// </remarks>
internal interface ISessionLockLabeler
{
    /// <summary>
    /// The labels for <paramref name="source" />'s locks.
    /// </summary>
    /// <param name="source">The resolved source and database the locks were read from.</param>
    /// <param name="classifier">
    /// The classifier over every registered source's declarations, or <see langword="null" /> when they could not all
    /// be read - or when only the source's own lock ids are wanted - which names no relation at all.
    /// </param>
    SessionLockLabels For(ResolvedSource source, DatabaseObjectClassifier? classifier);
}
