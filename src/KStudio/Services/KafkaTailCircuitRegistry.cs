namespace KStudio.Services;

/// <summary>
/// The live tail pages of one circuit that want to hear when its connection drops and returns (D46). Scoped, so it is the circuit's own: a page registers
/// while it is alive and <see cref="KafkaTailCircuitHandler" /> tells it. A circuit with no tail page holds an empty registry and nothing happens.
/// </summary>
internal sealed class KafkaTailCircuitRegistry
{
    private readonly object gate = new();
    private readonly List<Func<bool, Task>> listeners = [];

    /// <summary>Registers a page's listener. Dispose the result when the page goes.</summary>
    /// <param name="listener">Called with <see langword="false" /> when the connection dropped and <see langword="true" /> when it is back.</param>
    public IDisposable Register(Func<bool, Task> listener)
    {
        ArgumentNullException.ThrowIfNull(listener);

        lock (gate)
        {
            listeners.Add(listener);
        }

        return new Registration(this, listener);
    }

    /// <summary>
    /// Tells every registered page that the connection dropped (<paramref name="up" /> false) or returned. This is what the circuit handler calls; it takes no
    /// <c>Circuit</c>, so a test drives it directly. A circuit with no page registered does nothing and allocates nothing.
    /// </summary>
    /// <param name="up">Whether the connection is up.</param>
    public Task ConnectionChangedAsync(bool up)
    {
        Func<bool, Task>[] snapshot;

        lock (gate)
        {
            if (listeners.Count == 0)
            {
                return Task.CompletedTask;
            }

            snapshot = [.. listeners];
        }

        return NotifyAsync(snapshot, up);
    }

    private static async Task NotifyAsync(Func<bool, Task>[] snapshot, bool up)
    {
        foreach (Func<bool, Task> listener in snapshot)
        {
            await listener(up);
        }
    }

    private sealed class Registration(KafkaTailCircuitRegistry owner, Func<bool, Task> listener) : IDisposable
    {
        public void Dispose()
        {
            lock (owner.gate)
            {
                owner.listeners.Remove(listener);
            }
        }
    }
}
