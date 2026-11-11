using System.Runtime.CompilerServices;

namespace KStudio.Integration.Tests;

/// <summary>
/// A fact that needs a Kafka container: it skips with a reason when Docker is not running, and never skips on CI
/// (see <see cref="DockerAvailability"/>).
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class KafkaFactAttribute : FactAttribute
{
    /// <summary>Wires up xunit's conditional skip against the Docker probe.</summary>
    /// <param name="sourceFilePath">Supplied by the compiler; xunit uses it to locate the test.</param>
    /// <param name="sourceLineNumber">Supplied by the compiler; xunit uses it to locate the test.</param>
    public KafkaFactAttribute(
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        Skip = DockerAvailability.SkipReason;
        SkipType = typeof(DockerAvailability);
        SkipUnless = nameof(DockerAvailability.IsAvailable);
    }
}

/// <summary>A theory that needs a Kafka container. Same rule as <see cref="KafkaFactAttribute"/>.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class KafkaTheoryAttribute : TheoryAttribute
{
    /// <summary>Wires up xunit's conditional skip against the Docker probe.</summary>
    /// <param name="sourceFilePath">Supplied by the compiler; xunit uses it to locate the test.</param>
    /// <param name="sourceLineNumber">Supplied by the compiler; xunit uses it to locate the test.</param>
    public KafkaTheoryAttribute(
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        Skip = DockerAvailability.SkipReason;
        SkipType = typeof(DockerAvailability);
        SkipUnless = nameof(DockerAvailability.IsAvailable);
    }
}
