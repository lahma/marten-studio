namespace MartenStudio.Tests.Support;

/// <summary>
/// The bound on a wait for a gate a test holds shut (1.2-HANG).
/// </summary>
/// <remarks>
/// A test that holds a <see cref="TaskCompletionSource" /> shut waits for it somewhere - in a fake the page calls, or in work it hands
/// to the service under test. xunit has no timeout of its own, so a gate nobody opens (a test that failed first, or two overlapping
/// runs of which the wrong one took it) is a test host that sits idle until a CI job's cancellation, with nothing saying which test.
/// Awaiting the gate through <see cref="Bounded(Task)" /> turns that into a <see cref="TimeoutException" /> on the test that held it.
/// <c>NoUnboundedGateAwaitTests</c> fails on an await of a gate that does not say so.
/// </remarks>
internal static class TestBounds
{
    /// <summary>Far longer than any gate is held for in a passing run, and short enough that the failure names its test.</summary>
    public static readonly TimeSpan Gate = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Waits for <paramref name="gate" /> for at most <see cref="Gate" />, then throws <see cref="TimeoutException" />. No token is
    /// forwarded on purpose: a fake that is cancelled with its caller must still see a gate a test opens after the caller left.
    /// </summary>
    /// <param name="gate">The gate's task.</param>
    public static Task Bounded(this Task gate) => gate.WaitAsync(Gate, CancellationToken.None);

    /// <summary>The generic form of <see cref="Bounded(Task)" />, answering what the gate was completed with.</summary>
    /// <typeparam name="T">What the gate yields.</typeparam>
    /// <param name="gate">The gate's task.</param>
    public static Task<T> Bounded<T>(this Task<T> gate) => gate.WaitAsync(Gate, CancellationToken.None);
}
