namespace KStudio.Tests.Support;

/// <summary>
/// The bound on a wait for a gate a test holds shut: this suite's copy of <c>MartenStudio.Tests.Support.TestBounds</c>. xunit has no timeout of
/// its own, so a gate nobody opens is a test host that sits idle until a CI job's cancellation; awaiting it through <c>Bounded()</c> turns that
/// into a <see cref="TimeoutException" /> on the test that held it. <c>KStudioNoUnboundedGateAwaitTests</c> scans this suite for an await that
/// does not say so.
/// </summary>
internal static class TestBounds
{
    /// <summary>Far longer than any gate is held for in a passing run, and short enough that the failure names its test.</summary>
    public static readonly TimeSpan Gate = TimeSpan.FromSeconds(30);

    /// <summary>Waits for <paramref name="gate" /> for at most <see cref="Gate" />, then throws <see cref="TimeoutException" />.</summary>
    /// <param name="gate">The gate's task.</param>
    public static Task Bounded(this Task gate) => gate.WaitAsync(Gate, CancellationToken.None);

    /// <summary>The generic form of <see cref="Bounded(Task)" />.</summary>
    /// <typeparam name="T">What the gate yields.</typeparam>
    /// <param name="gate">The gate's task.</param>
    public static Task<T> Bounded<T>(this Task<T> gate) => gate.WaitAsync(Gate, CancellationToken.None);
}
