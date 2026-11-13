namespace KStudio.Tests.Index;

/// <summary>
/// A hand-written host record index (the family uses no mocking library): it records every question it is asked and answers from a script. The
/// default answer is <see cref="KafkaIndexAnswer.None" />.
/// </summary>
internal sealed class FakeRecordIndex : IKafkaRecordIndex
{
    private readonly List<KafkaIndexQuery> queries = [];
    private readonly Lock gate = new();

    /// <summary>What the index answers; replaced by a test.</summary>
    public Func<KafkaIndexQuery, CancellationToken, ValueTask<KafkaIndexAnswer>> Answer { get; set; } = static (_, _) => ValueTask.FromResult(KafkaIndexAnswer.None);

    /// <summary>The questions asked so far, in order.</summary>
    public IReadOnlyList<KafkaIndexQuery> Queries
    {
        get
        {
            lock (gate)
            {
                return [.. queries];
            }
        }
    }

    /// <summary>Answers with these hints, whatever is asked.</summary>
    /// <param name="hints">The hints.</param>
    public FakeRecordIndex Hints(params KafkaIndexHint[] hints)
    {
        Answer = (_, _) => ValueTask.FromResult(KafkaIndexAnswer.Hints(hints));
        return this;
    }

    /// <inheritdoc />
    public ValueTask<KafkaIndexAnswer> FindAsync(KafkaIndexQuery query, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            queries.Add(query);
        }

        return Answer(query, cancellationToken);
    }
}
