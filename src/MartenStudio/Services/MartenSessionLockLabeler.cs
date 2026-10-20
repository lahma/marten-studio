using MartenStudio.Services.Database;
using MartenStudio.Services.Sources;

namespace MartenStudio.Services;

/// <summary>
/// MartenStudio's <see cref="ISessionLockLabeler" />: the store's daemon leadership and shard keys and its schema-apply
/// lock, from its options (<see cref="SessionLockLabels.For" />). PS-1a's pass-through; the database packet (PS-1b)
/// moves the Marten half of <c>SessionLockLabels</c> behind it.
/// </summary>
internal sealed class MartenSessionLockLabeler : ISessionLockLabeler
{
    /// <inheritdoc />
    /// <exception cref="ArgumentException"><paramref name="source" /> is not a Marten store's.</exception>
    public SessionLockLabels For(ResolvedSource source, DatabaseObjectClassifier? classifier)
    {
        ArgumentNullException.ThrowIfNull(source);

        return source is ResolvedScope resolved
            ? SessionLockLabels.For(resolved.Store, resolved.Registration.Key, classifier)
            : throw new ArgumentException("MartenStudio labels the locks of a Marten store only.", nameof(source));
    }
}
