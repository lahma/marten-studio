namespace KStudio;

/// <summary>
/// A host's own answer to "where is this key, or this correlation value?" (AGENTS.md D48): offset ranges that narrow a search. KStudio ships no
/// implementation and keeps no index; a host implements this over data it already has, and registers it with
/// <see cref="KafkaEngineOptions.RecordIndex{TIndex}" />. A hint only narrows where a search reads: the search still reads and judges every record in
/// the ranges, and a hint that is wrong finds nothing, never something false.
/// </summary>
public interface IKafkaRecordIndex
{
    /// <summary>
    /// Returns where records matching the query may be, or <see cref="KafkaIndexAnswer.None" />. It is called once while a search is planned, with
    /// KStudio's request timeout: a failure or a timeout is ignored and the search reads its whole window.
    /// </summary>
    /// <param name="query">What the search looks for: one value of one field, in a window, on topics the visitor may search.</param>
    /// <param name="cancellationToken">Cancelled when the planning is abandoned or the request timeout passes.</param>
    ValueTask<KafkaIndexAnswer> FindAsync(KafkaIndexQuery query, CancellationToken cancellationToken);
}
