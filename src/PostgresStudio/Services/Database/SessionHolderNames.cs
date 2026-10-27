using PostgresStudio.Services.Sources;

namespace PostgresStudio.Services.Database;

/// <summary>
/// Which of the server's other databases Database › Sessions › Holders may name to a visitor short of
/// <c>ViewDatabaseActivity</c> (issue #20, D29, D30).
/// </summary>
/// <remarks>
/// <para>
/// <b>The rule, in one sentence.</b> A database of the server is named when it is the one the studio's connection is on - the
/// scope the visitor's whole-database gate already passed - or when a database a registered source holds is that database, on
/// that server, and the source policy (<c>StudioAuthorization.IsAuthorizedAsync</c> with <c>TenantId = null</c> and no
/// capability, asked about the source's registered key and the database's own identity) admits it. Every other database -
/// one the policy refuses, and one no source registers - is counted in "Other databases", with no name. A visitor the
/// <c>ViewDatabaseActivity</c> gate passes is named every database and never reaches this class: that capability is
/// the server's "who is using it" power, and the Holders view's other databases are the same kind of fact as this one's
/// applications, roles and client addresses.
/// </para>
/// <para>
/// <b>Why by the policy and not by the visitor's own picker.</b> The picker (<see cref="IStudioScopeCatalog" />) enumerates
/// every source, a dynamic tenancy included, which for a Marten tenancy that builds its databases is not read-only (D27's known
/// limit). This asks the same question - may the visitor see <em>this</em> database - of the same policy, about the same
/// resource (<see cref="StudioScope" />, never the visitor's spelling), but only of the databases that answer a name on this
/// server, and never enumerates a <see cref="StudioDatabaseCardinality.DynamicMultiple" /> source: a host-provided source
/// of tenant databases, or a tenancy that builds them, is left out, and its databases are counted unnamed. That is the
/// cost of D27, and the safe side of it.
/// </para>
/// <para>
/// <b>By name and server.</b> A database's identity (D30) is its source's own - a registration name, a Marten database id - and
/// is never the name Postgres knows it by, so the match is on <see cref="IStudioDatabase.Name" /> (the connection string's
/// database) and on the server the connection string points at (<see cref="DatabasePoolSettings.ServerKey" />, host and port
/// as C02 keys them): a database of the same name on another server is another database. A source that cannot say its
/// server, or whose connection string will not parse, names nothing. Names are compared ordinally: Postgres' are
/// case-sensitive.
/// </para>
/// <para>
/// <b>Fails closed.</b> A source that will not build or list, or a policy handler that throws about a database, leaves that
/// database unnamed; nothing is logged, because the answer is "counted, not named", which is the page's ordinary state, and
/// the failure has its own events where it is the page's own (9210, 9213). Scoped: the policy is about this circuit's visitor,
/// and nothing here is remembered.
/// </para>
/// </remarks>
internal sealed class SessionHolderNames(IStudioSourceProvider sources, StudioAuthorization authorization)
{
    /// <summary>
    /// The names among <paramref name="databases" /> this visitor's source policy admits them to, on the server
    /// <paramref name="resolved" /> is connected to.
    /// </summary>
    /// <param name="resolved">The database the visitor's scope resolved to, whose server the read was made on.</param>
    /// <param name="databases">The names of the other databases that have sessions on the server - the studio's own is not asked.</param>
    /// <param name="cancellationToken">Cancels the question.</param>
    public async Task<IReadOnlySet<string>> AdmittedAsync(
        ResolvedSource resolved,
        IEnumerable<string> databases,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resolved);
        ArgumentNullException.ThrowIfNull(databases);

        HashSet<string> wanted = [.. databases.Where(static x => x.Length > 0)];
        HashSet<string> admitted = new(StringComparer.Ordinal);

        // Nothing else on the server has a session, or this database's server cannot be told: nothing is asked of any source.
        if (wanted.Count == 0 || DatabasePoolSettings.Of(resolved.Target)?.ServerKey is not { } server)
        {
            return admitted;
        }

        foreach (StudioSourceRegistration registration in sources.Registrations())
        {
            cancellationToken.ThrowIfCancellationRequested();

            IReadOnlyList<IStudioDatabase> held;

            try
            {
                if (!sources.TryResolve(registration, out IStudioSource? source).IsAvailable
                    || source is null
                    || source.Cardinality == StudioDatabaseCardinality.DynamicMultiple)
                {
                    continue;
                }

                held = await source.DatabasesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                continue;
            }

            foreach (IStudioDatabase database in held)
            {
                if (!wanted.Contains(database.Name)
                    || admitted.Contains(database.Name)
                    || DatabasePoolSettings.Of(database)?.ServerKey != server)
                {
                    continue;
                }

                try
                {
                    // By the source's registered key and the database's own identity (D29), with no tenant and no capability.
                    if (await authorization.IsAuthorizedAsync(new StudioScope(registration.Key, database.Identity, null), capability: null, cancellationToken)
                            .ConfigureAwait(false))
                    {
                        admitted.Add(database.Name);
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // A policy that cannot answer has not said yes.
                }
            }
        }

        return admitted;
    }
}
