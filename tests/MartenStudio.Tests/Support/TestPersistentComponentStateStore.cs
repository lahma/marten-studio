using System.Text.Json;

using Microsoft.AspNetCore.Components;

namespace MartenStudio.Tests.Support;

/// <summary>
/// Stands in for the prerender-to-circuit hand-off: what a component persisted on one side and reads on
/// the other.
/// </summary>
/// <remarks>
/// The real store is the Blazor Server circuit's, and it is what carries the prerendered scope's answers
/// into the circuit's own DI scope. A test that wants to say "the server already knew the theme was dark"
/// has to say it the way the framework does, or it proves nothing about the component.
/// </remarks>
public sealed class TestPersistentComponentStateStore : IPersistentComponentStateStore
{
    private Dictionary<string, byte[]> entries = new(StringComparer.Ordinal);

    /// <summary>Puts a value in as JSON, the way <c>PersistAsJson</c> writes it.</summary>
    public void Seed<TValue>(string key, TValue value) =>
        entries[key] = JsonSerializer.SerializeToUtf8Bytes(value, JsonSerializerOptions.Web);

    /// <summary>Reads a persisted value back out as the framework wrote it.</summary>
    public TValue? Read<TValue>(string key) =>
        entries.TryGetValue(key, out byte[]? json) ? JsonSerializer.Deserialize<TValue>(json, JsonSerializerOptions.Web) : default;

    public Task<IDictionary<string, byte[]>> GetPersistedStateAsync() =>
        Task.FromResult<IDictionary<string, byte[]>>(entries);

    public Task PersistStateAsync(IReadOnlyDictionary<string, byte[]> state)
    {
        entries = state.ToDictionary(static x => x.Key, static x => x.Value, StringComparer.Ordinal);
        return Task.CompletedTask;
    }
}
