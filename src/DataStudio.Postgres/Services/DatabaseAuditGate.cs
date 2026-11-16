using System.Security.Claims;

using DataStudio.Services;
using DataStudio.Postgres.Services.Settings;

namespace DataStudio.Postgres.Services;

/// <summary>
/// Decides which of the database module's audit entries a visitor may read (D34): the half of the old Activity sweep that was never about a
/// module, moved here from <c>StudioActionLog</c> with its questions, their order and their memory unchanged.
/// </summary>
/// <remarks>
/// <para>
/// <b>The store policy, entry by entry</b>, against the store, database and tenant the action was aimed at: the ring is process-wide and
/// every circuit writes into it, so it is the one place one visitor's actions can be read by another. An entry whose scope was never settled
/// carries a placeholder store key, which no policy recognises; that is the safe direction.
/// </para>
/// <para>
/// <b>And a read beyond the store only for somebody who may make it.</b> An entry recorded under <c>BrowseDatabase</c> or <c>RunSql</c>
/// names what was read - a non-Marten table and the columns a filter was on, up to two hundred characters of a statement - and the store
/// policy says nothing about who may learn those: a visitor the write policy refuses the browser would read, here, the tables and columns an
/// operator browsed. So such an entry is kept only while the capability is on for the process and the write policy, asked with the capability
/// named against the entry's own scope, passes this visitor - the question the service asked before the read could run
/// (<see cref="StudioCapabilityGuard.ReadsBeyondTheStore" />).
/// </para>
/// <para>
/// <b>Each question is asked once per sweep:</b> the answers are remembered for the sweep by store, database, tenant and capability, because
/// the ring is five hundred entries that are nearly always about a handful of scopes, and a panel refreshed every interval must not pay a
/// thousand policy evaluations to draw fifteen rows. Only for the sweep: the next one asks again, and the answers are this visitor's.
/// </para>
/// </remarks>
internal sealed class DatabaseAuditGate : IStudioAuditScopeGate
{
    private readonly StudioAuthorization authorization;
    private readonly StudioCapabilityGuard capabilities;
    private readonly StudioDescriptor descriptor;

    /// <param name="authorization">The circuit's policy evaluator.</param>
    /// <param name="capabilities">Which capabilities the process has on.</param>
    /// <param name="descriptor">The studio's descriptor, whose key is the database module's.</param>
    public DatabaseAuditGate(StudioAuthorization authorization, StudioCapabilityGuard capabilities, StudioDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(descriptor);

        this.authorization = authorization;
        this.capabilities = capabilities;
        this.descriptor = descriptor;
    }

    /// <inheritdoc />
    public string Kind => descriptor.Key;

    /// <inheritdoc />
    public IStudioAuditSweep BeginSweep(Func<ValueTask<ClaimsPrincipal>> principal)
    {
        ArgumentNullException.ThrowIfNull(principal);

        return new Sweep(principal, authorization, capabilities);
    }

    /// <summary>
    /// One policy question of a sweep: compared ordinally on every part, so two entries share an answer only when the policy would have been
    /// shown the very same resource.
    /// </summary>
    private readonly record struct SweepQuestion(string StoreKey, string DatabaseId, string? TenantId, string? Capability);

    /// <summary>The read beyond the store an entry was recorded under, or <see langword="null" />.</summary>
    private static StudioCapability? ReadBeyondTheStore(StudioActionLogEntry entry)
    {
        foreach (StudioCapability capability in StudioCapabilityGuard.ReadsBeyondTheStore)
        {
            if (string.Equals(entry.Capability, capability.ToString(), StringComparison.Ordinal))
            {
                return capability;
            }
        }

        return null;
    }

    private sealed class Sweep : IStudioAuditSweep
    {
        private readonly Func<ValueTask<ClaimsPrincipal>> principal;
        private readonly StudioAuthorization authorization;
        private readonly StudioCapabilityGuard capabilities;
        private readonly bool storePolicy;
        private readonly Dictionary<SweepQuestion, bool> answers = [];

        public Sweep(Func<ValueTask<ClaimsPrincipal>> principal, StudioAuthorization authorization, StudioCapabilityGuard capabilities)
        {
            this.principal = principal;
            this.authorization = authorization;
            this.capabilities = capabilities;
            storePolicy = !string.IsNullOrWhiteSpace(authorization.PolicyFor(null));
        }

        public async ValueTask<bool> IsVisibleAsync(StudioActionLogEntry entry, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(entry);

            // An entry of this kind that carries another module's value is not ours to judge: fail closed.
            if (entry.DatabaseScope() is not { } databaseScope)
            {
                return false;
            }

            StudioScope scope = new(databaseScope.StoreKey, databaseScope.DatabaseId, databaseScope.TenantId);

            if (storePolicy && !await AskAsync(scope, capability: null, cancellationToken).ConfigureAwait(false))
            {
                return false;
            }

            if (ReadBeyondTheStore(entry) is { } capability)
            {
                // The read an entry names is the visitor's to see if it, or a capability that implies it
                // (BrowseDatabase implies ViewDefinitions), is on for the process and the write policy passes them.
                bool mayRead = false;

                foreach (StudioCapability candidate in (StudioCapability[]) [capability, .. StudioCapabilityGuard.ImpliedBy(capability)])
                {
                    if (!capabilities.IsEnabled(candidate))
                    {
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(authorization.PolicyFor(candidate.ToString()))
                        || await AskAsync(scope, candidate.ToString(), cancellationToken).ConfigureAwait(false))
                    {
                        mayRead = true;
                        break;
                    }
                }

                if (!mayRead)
                {
                    return false;
                }
            }

            return true;
        }

        private async ValueTask<bool> AskAsync(StudioScope scope, string? capability, CancellationToken cancellationToken)
        {
            SweepQuestion question = new(scope.StoreKey, scope.DatabaseId, scope.TenantId, capability);

            if (answers.TryGetValue(question, out bool known))
            {
                return known;
            }

            ClaimsPrincipal user = await principal().ConfigureAwait(false);

            bool answer = await authorization.IsAuthorizedAsync(user, scope, capability, cancellationToken).ConfigureAwait(false);
            answers[question] = answer;
            return answer;
        }
    }
}
