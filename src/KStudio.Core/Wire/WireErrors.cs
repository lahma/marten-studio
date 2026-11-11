namespace KStudio.Core.Wire;

/// <summary>An operation the engine can be refused for by an ACL (AGENTS.md D36, decision 5.4).</summary>
internal enum KafkaAclOperation
{
    /// <summary><c>READ</c>.</summary>
    Read,

    /// <summary><c>DESCRIBE</c>.</summary>
    Describe,

    /// <summary><c>DESCRIBE_CONFIGS</c>.</summary>
    DescribeConfigs,
}

/// <summary>The kind of resource an ACL protects.</summary>
internal enum KafkaResourceType
{
    /// <summary>A topic.</summary>
    Topic,

    /// <summary>A consumer group.</summary>
    Group,

    /// <summary>The cluster.</summary>
    Cluster,
}

/// <summary>Which wire call an error came from, so that the mapping can tell a refused fetch from a refused describe.</summary>
internal enum WireCall
{
    /// <summary>Metadata for all topics.</summary>
    Metadata,

    /// <summary>Describe topics.</summary>
    DescribeTopics,

    /// <summary>Describe the cluster.</summary>
    DescribeCluster,

    /// <summary>List offsets.</summary>
    ListOffsets,

    /// <summary>Describe a topic's configuration.</summary>
    DescribeConfigs,

    /// <summary>A group's committed offsets.</summary>
    GroupOffsets,

    /// <summary>Describe a group.</summary>
    DescribeGroup,

    /// <summary>A fetch by a scan reader.</summary>
    Fetch,
}

/// <summary>
/// An error as a value (decision 3.3): what a wire call returns instead of throwing, with no Confluent type in it.
/// </summary>
internal abstract record WireError
{
    private WireError()
    {
    }

    /// <summary>The name of the kind of error, for logs.</summary>
    public string Kind => GetType().Name;

    /// <summary>A code for logs: the Confluent <c>ErrorCode</c> name where there is one, else the kind.</summary>
    public virtual string Code => Kind;

    /// <summary>The broker refused for an ACL.</summary>
    /// <param name="Operation">The operation that was refused.</param>
    /// <param name="ResourceType">The resource's kind.</param>
    /// <param name="ResourceName">The resource's name.</param>
    public sealed record NotAuthorized(KafkaAclOperation Operation, KafkaResourceType ResourceType, string ResourceName) : WireError;

    /// <summary>SASL or TLS authentication failed.</summary>
    /// <param name="AuthCode">The Confluent <c>ErrorCode</c> name.</param>
    public sealed record AuthenticationFailed(string AuthCode) : WireError
    {
        /// <inheritdoc />
        public override string Code => AuthCode;
    }

    /// <summary>The broker has no version of this API.</summary>
    /// <param name="Api">The API's name.</param>
    public sealed record Unsupported(string Api) : WireError;

    /// <summary>A position is below the log start.</summary>
    /// <param name="Partition">The partition.</param>
    /// <param name="Position">The position that was asked for, when known.</param>
    public sealed record OffsetOutOfRange(WirePartition Partition, long? Position) : WireError;

    /// <summary>An unknown topic, partition or group.</summary>
    /// <param name="ResourceType">The resource's kind.</param>
    /// <param name="Name">The resource's name.</param>
    public sealed record NotFound(KafkaResourceType ResourceType, string Name) : WireError;

    /// <summary>The cluster did not answer: network, timeouts, no leader, no coordinator.</summary>
    /// <param name="UnavailableCode">The Confluent <c>ErrorCode</c> name.</param>
    /// <param name="Retriable">Whether trying again may help.</param>
    public sealed record Unavailable(string UnavailableCode, bool Retriable) : WireError
    {
        /// <inheritdoc />
        public override string Code => UnavailableCode;
    }

    /// <summary>Anything else.</summary>
    /// <param name="FailedCode">The Confluent <c>ErrorCode</c> name.</param>
    /// <param name="IsFatal">Whether the client cannot go on.</param>
    public sealed record Failed(string FailedCode, bool IsFatal) : WireError
    {
        /// <inheritdoc />
        public override string Code => FailedCode;
    }
}

/// <summary>
/// What a scan reader's non-reading calls (<c>Assign</c>, <c>Pause</c>, <c>Resume</c>, <c>Reposition</c>) and the construction of a
/// reader throw instead of a Confluent exception, so that the scan's thread catches an engine type and never a Confluent one. The
/// admin calls return their errors as values; a reader's calls are void, so the value travels in this exception.
/// </summary>
/// <param name="error">The mapped error.</param>
internal sealed class WireException(WireError error) : Exception("The wire call failed: " + error?.Code + ".")
{
    /// <summary>The error, mapped from the client's.</summary>
    public WireError Error { get; } = error ?? throw new ArgumentNullException(nameof(error));
}

/// <summary>A wire call's outcome: a value, or the error that withheld it. Exactly one is set.</summary>
/// <typeparam name="T">What the call returns.</typeparam>
internal readonly struct WireResult<T>
{
    private readonly T? value;

    private WireResult(T? value, WireError? error)
    {
        this.value = value;
        Error = error;
    }

    /// <summary>The error, or <see langword="null" /> when the call succeeded.</summary>
    public WireError? Error { get; }

    /// <summary>Whether the call succeeded.</summary>
    public bool IsOk => Error is null;

    /// <summary>The value of a successful call.</summary>
    public T Value => Error is null ? value! : throw new InvalidOperationException("The wire call failed: " + Error.Code + ".");

    /// <summary>A successful result.</summary>
    /// <param name="value">What the call returned.</param>
    public static WireResult<T> Ok(T value) => new(value, null);

    /// <summary>A failed result.</summary>
    /// <param name="error">Why.</param>
    public static WireResult<T> Fail(WireError error) => new(default, error);
}
