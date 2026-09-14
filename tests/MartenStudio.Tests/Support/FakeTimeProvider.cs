namespace MartenStudio.Tests.Support;

/// <summary>
/// A clock a test moves by hand, so a cache window can be tested without waiting for one.
/// </summary>
/// <remarks>
/// Hand-written rather than taken from <c>Microsoft.Extensions.TimeProvider.Testing</c>: the package
/// budget is complete (AGENTS.md hard rule 1) and this is fourteen lines. Only <c>GetUtcNow</c> is
/// overridden, which is all the studio's caches read.
/// </remarks>
public sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
{
    private DateTimeOffset utcNow = now;

    public override DateTimeOffset GetUtcNow() => utcNow;

    /// <summary>Moves the clock forward.</summary>
    public void Advance(TimeSpan by) => utcNow = utcNow.Add(by);
}
