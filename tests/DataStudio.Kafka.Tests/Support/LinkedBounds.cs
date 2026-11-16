namespace DataStudio.Postgres.Tests;

/// <summary>
/// <c>Bounded()</c> for the linked <c>PostgresFakes.cs</c>, which awaits its gates through the other suite's <c>TestBounds</c> in its own namespace: this
/// forwards to this suite's copy, so the linked file compiles unchanged.
/// </summary>
internal static class LinkedBounds
{
    public static Task Bounded(this Task gate) => DataStudio.Kafka.Tests.Support.TestBounds.Bounded(gate);

    public static Task<T> Bounded<T>(this Task<T> gate) => DataStudio.Kafka.Tests.Support.TestBounds.Bounded(gate);
}
