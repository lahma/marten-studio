using KStudio.Core.Clusters;
using KStudio.Core.Decoding;

namespace KStudio.Services;

/// <summary>Whether a record's value may be shown to the visitor, and if not, which sentence says why (D-PAYLOAD decision 1.3).</summary>
internal enum KafkaValueAccess
{
    /// <summary>The scan or read had the payload grant: the value is in the match, as far as the engine could keep it.</summary>
    Granted,

    /// <summary><c>ReadPayloads</c> is off, or the host studio's <c>ReadOnly</c> turned it off.</summary>
    CapabilityOff,

    /// <summary>The capability policy refused <c>ReadPayloads</c> for the record's topic.</summary>
    PolicyRefused,

    /// <summary>The search ran under the envelope grant only, because another of its topics did not allow values.</summary>
    SearchWithoutValues,
}

/// <summary>What the grant service decided.</summary>
/// <param name="Grant">What the engine may read: <see cref="KafkaReadGrant.Envelope" />, or <c>Envelope | Payload</c>.</param>
/// <param name="Refusal">The sentence of a search refused whole, or <see langword="null" />.</param>
/// <param name="Denied">The capability refusal of a search that reads values with <c>ReadPayloads</c> off, or <see langword="null" />.</param>
/// <param name="TopicsWithoutValues">The topics whose capability policy refused <c>ReadPayloads</c>, for the detail's line.</param>
/// <param name="RefusedTopic">The first topic a refusal came from, for the audit; it is shown nowhere.</param>
/// <param name="CapabilityOff">Whether <c>ReadPayloads</c> is off (or turned off by <c>ReadOnly</c>).</param>
internal sealed record KafkaGrantDecision(
    KafkaReadGrant Grant,
    string? Refusal,
    KafkaCapabilityDeniedException? Denied,
    IReadOnlySet<string> TopicsWithoutValues,
    string? RefusedTopic = null,
    bool CapabilityOff = false)
{
    /// <summary>The decision of a context that has none yet: envelope only.</summary>
    public static KafkaGrantDecision EnvelopeOnly { get; } = new(KafkaReadGrant.Envelope, null, null, new HashSet<string>(StringComparer.Ordinal), CapabilityOff: true);

    /// <summary>Whether the visitor may read values: the grant includes the payload.</summary>
    public bool HasPayload => Grant.HasFlag(KafkaReadGrant.Payload);

    /// <summary>Whether this decision refuses the request.</summary>
    public bool IsRefused => Refusal is not null || Denied is not null;

    /// <summary>Whether this decision gives at least what <paramref name="other" /> gave.</summary>
    /// <param name="other">The earlier decision.</param>
    public bool AtLeast(KafkaGrantDecision other) =>
        !IsRefused && (Grant & other.Grant) == other.Grant;

    /// <summary>How a record of <paramref name="topic" /> reads in this decision.</summary>
    /// <param name="topic">The record's topic.</param>
    public KafkaValueAccess AccessFor(string topic) =>
        HasPayload ? KafkaValueAccess.Granted
        : CapabilityOff ? KafkaValueAccess.CapabilityOff
        : TopicsWithoutValues.Contains(topic) ? KafkaValueAccess.PolicyRefused
        : KafkaValueAccess.SearchWithoutValues;
}

/// <summary>
/// Decides the engine's <see cref="KafkaReadGrant" /> after the <c>SearchRecords</c> gates have passed (D-PAYLOAD decision 1.2): <c>Envelope</c>, or
/// <c>Envelope | Payload</c> when <c>ReadPayloads</c> is on and the capability policy passes it for every topic. Asking whether values may be kept, and
/// being told no, refuses nothing and records nothing; only a search that <i>reads</i> values is refused, and it is refused whole.
/// </summary>
/// <param name="capabilities">The capability switch.</param>
/// <param name="authorization">The cluster and capability policies.</param>
internal sealed class KafkaReadGrants(KafkaCapabilityGuard capabilities, KafkaAuthorization authorization)
{
    /// <summary>The grant for a search.</summary>
    /// <param name="cluster">The cluster entry in scope.</param>
    /// <param name="topics">The topics the search names.</param>
    /// <param name="readsValues">Whether the predicate reads values (<c>value</c> or a JSON path).</param>
    /// <param name="cancellationToken">Abandons the question.</param>
    public async Task<KafkaGrantDecision> ForSearchAsync(
        KafkaClusterEntry cluster,
        IReadOnlyList<KafkaTopicName> topics,
        bool readsValues,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cluster);
        ArgumentNullException.ThrowIfNull(topics);

        if (!capabilities.IsEnabled(KafkaCapability.ReadPayloads))
        {
            if (!readsValues)
            {
                return new KafkaGrantDecision(KafkaReadGrant.Envelope, null, null, new HashSet<string>(StringComparer.Ordinal), CapabilityOff: true);
            }

            try
            {
                capabilities.RequireOne(KafkaCapability.ReadPayloads);
            }
            catch (KafkaCapabilityDeniedException denial)
            {
                return new KafkaGrantDecision(KafkaReadGrant.Envelope, denial.Message, denial, new HashSet<string>(StringComparer.Ordinal), CapabilityOff: true);
            }
        }

        HashSet<string> without = new(StringComparer.Ordinal);
        string? first = null;

        foreach (KafkaTopicName topic in topics)
        {
            if (!await authorization.IsAuthorizedAsync(cluster, topic.Value, KafkaCapability.ReadPayloads, cancellationToken).ConfigureAwait(false))
            {
                without.Add(topic.Value);
                first ??= topic.Value;
            }
        }

        if (without.Count == 0)
        {
            return new KafkaGrantDecision(KafkaReadGrant.Envelope | KafkaReadGrant.Payload, null, null, without);
        }

        return readsValues
            ? new KafkaGrantDecision(KafkaReadGrant.Envelope, KafkaStudioText.SearchValuesRefusedTopic, null, without, first)
            : new KafkaGrantDecision(KafkaReadGrant.Envelope, null, null, without, first);
    }

    /// <summary>The grant for the record page: the same question for one topic.</summary>
    /// <param name="cluster">The cluster entry in scope.</param>
    /// <param name="topic">The record's topic.</param>
    /// <param name="cancellationToken">Abandons the question.</param>
    public async Task<KafkaGrantDecision> ForRecordAsync(KafkaClusterEntry cluster, KafkaTopicName topic, CancellationToken cancellationToken) =>
        await ForSearchAsync(cluster, [topic], readsValues: false, cancellationToken).ConfigureAwait(false);
}
