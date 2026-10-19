using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using MartenStudio.Services.Live;

using Microsoft.JSInterop;

namespace MartenStudio.Services.Health;

/// <summary>
/// The findings this browser has muted: kept in the browser's <c>localStorage</c> as hashes, read once per circuit.
/// </summary>
/// <remarks>
/// <para>
/// <b>Per visitor, and nowhere else.</b> A mute is one person's "I know about this one", so it lives in their
/// browser (<c>martenStudio.health</c> in <c>js/marten-studio.js</c>) and the studio writes nothing to the
/// host's database or to any other visitor's view. A mute key is the store, the database and the finding's
/// key, so muting a finding about one database leaves the same code on another alone.
/// </para>
/// <para>
/// <b>Hashed before it leaves the circuit.</b> A finding's key can carry what its gate guards - E03's subject is
/// <c>tenant/streamName</c> - and <c>localStorage</c> is readable by every script on the host's origin and by the next
/// person at a shared machine. So the browser is handed, and keeps, only the SHA-256 of each key, as lowercase hex
/// (<see cref="Hash" />), and a key is muted when its hash is in the list; no raw key reaches a JS call. It is a
/// plain hash: it takes the ids off the list, and it does not stop somebody who can already guess a whole key from
/// confirming the guess, which is all a mute is worth.
/// </para>
/// <para>
/// <b>Correct without it.</b> A private window, blocked storage, a prerender or a closed tab all answer as an
/// empty list, and every access is behind <see cref="StudioLiveUpdates.IsInteropUnavailable" /> on this side
/// and a <c>try</c> on the browser's: what that costs is that nothing is muted, never a broken page.
/// </para>
/// <para>
/// Scoped: one circuit is one browser tab. Bounded at <see cref="MaxKeys" />, newest kept, so the list in
/// <c>localStorage</c> cannot grow for ever.
/// </para>
/// </remarks>
internal sealed class HealthMutes
{
    /// <summary>The most mute keys a browser keeps.</summary>
    public const int MaxKeys = 200;

    /// <summary>How long a stored entry is: a SHA-256, in hex.</summary>
    private const int HashLength = 64;

    private readonly List<string> hashes = [];
    private readonly Lock gate = new();
    private bool loaded;

    /// <summary>Raised after the list changed, so the sidebar's badge and the page both redraw.</summary>
    public event Func<Task>? Changed;

    /// <summary>Whether the browser's list has been read yet.</summary>
    public bool IsLoaded => loaded;

    /// <summary>Every muted key's hash (<see cref="Hash" />), oldest first - what the browser keeps.</summary>
    public IReadOnlyList<string> Keys
    {
        get
        {
            lock (gate)
            {
                return [.. hashes];
            }
        }
    }

    /// <summary>What the browser keeps for <paramref name="muteKey" />: its SHA-256 over UTF-8, as lowercase hex.</summary>
    internal static string Hash(string muteKey)
    {
        ArgumentNullException.ThrowIfNull(muteKey);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(muteKey)));
    }

    /// <summary>Whether <paramref name="muteKey" /> is muted in this browser.</summary>
    public bool IsMuted(string muteKey)
    {
        string hash = Hash(muteKey);

        lock (gate)
        {
            return hashes.Contains(hash, StringComparer.Ordinal);
        }
    }

    /// <summary>Reads the browser's list, once per circuit. A browser that cannot answer has muted nothing.</summary>
    /// <remarks>
    /// A list that held anything but hashes - in practice the raw keys a pre-release 0.4 build stored - is written back
    /// at once without it, so a raw key does not wait in <c>localStorage</c> for the next mute to overwrite it. Those
    /// mutes are dropped, not carried over: nothing is migrated.
    /// </remarks>
    public async Task LoadAsync(IJSRuntime jsRuntime, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(jsRuntime);

        if (loaded)
        {
            return;
        }

        string? stored = null;

        try
        {
            stored = await jsRuntime.InvokeAsync<string?>("martenStudio.health.getMuted", cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (StudioLiveUpdates.IsInteropUnavailable(exception))
        {
            // Prerendering, blocked storage, a tab that has gone: nothing is muted.
        }

        IReadOnlyList<string> kept = Parse(stored, out bool dropped);
        string? cleaned = null;

        lock (gate)
        {
            hashes.Clear();
            hashes.AddRange(kept);
            loaded = true;

            if (dropped)
            {
                cleaned = JsonSerializer.Serialize(hashes);
            }
        }

        if (cleaned is not null)
        {
            await WriteAsync(jsRuntime, cleaned, cancellationToken).ConfigureAwait(false);
        }

        await RaiseChangedAsync().ConfigureAwait(false);
    }

    /// <summary>Mutes or unmutes one finding in this browser, and writes the list back.</summary>
    public async Task SetAsync(IJSRuntime jsRuntime, string muteKey, bool muted, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(jsRuntime);
        ArgumentException.ThrowIfNullOrEmpty(muteKey);

        string hash = Hash(muteKey);
        string json;

        lock (gate)
        {
            hashes.RemoveAll(x => string.Equals(x, hash, StringComparison.Ordinal));

            if (muted)
            {
                hashes.Add(hash);

                while (hashes.Count > MaxKeys)
                {
                    hashes.RemoveAt(0);
                }
            }

            json = JsonSerializer.Serialize(hashes);
        }

        await WriteAsync(jsRuntime, json, cancellationToken).ConfigureAwait(false);
        await RaiseChangedAsync().ConfigureAwait(false);
    }

    /// <summary>Hands the browser the list of hashes to keep. A browser that cannot keep it simply forgets.</summary>
    private static async Task WriteAsync(IJSRuntime jsRuntime, string json, CancellationToken cancellationToken)
    {
        try
        {
            await jsRuntime.InvokeVoidAsync("martenStudio.health.setMuted", cancellationToken, json).ConfigureAwait(false);
        }
        catch (Exception exception) when (StudioLiveUpdates.IsInteropUnavailable(exception))
        {
            // The mute holds for this circuit and is simply not remembered by the browser.
        }
    }

    /// <summary>The stored JSON list of hashes, or nothing for anything that is not one.</summary>
    internal static IReadOnlyList<string> Parse(string? stored) => Parse(stored, out _);

    /// <summary>
    /// The stored JSON list of hashes, newest <see cref="MaxKeys" />, and whether anything stored was left out: an
    /// entry that is not a SHA-256 in lowercase hex (a raw key from a pre-release build, dropped rather than
    /// migrated), a duplicate, one past the bound, or a value that is not a list at all.
    /// </summary>
    internal static IReadOnlyList<string> Parse(string? stored, out bool dropped)
    {
        dropped = false;

        if (string.IsNullOrWhiteSpace(stored))
        {
            return [];
        }

        string?[]? parsed;

        try
        {
            parsed = JsonSerializer.Deserialize<string?[]>(stored);
        }
        catch (JsonException)
        {
            dropped = true;
            return [];
        }

        if (parsed is null)
        {
            dropped = true;
            return [];
        }

        List<string> kept = [.. parsed.OfType<string>().Where(IsHash).Distinct(StringComparer.Ordinal).TakeLast(MaxKeys)];
        dropped = kept.Count != parsed.Length;
        return kept;
    }

    /// <summary>Whether <paramref name="entry" /> has the shape <see cref="Hash" /> gives: 64 lowercase hex digits.</summary>
    private static bool IsHash(string entry) =>
        entry.Length == HashLength && entry.All(static c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    /// <summary>
    /// Tells every listener, one at a time, and lets none of them break the others - the
    /// <see cref="ToastService" /> rule: a redraw that failed is a redraw, not a reason to lose the mute.
    /// </summary>
    private async Task RaiseChangedAsync()
    {
        foreach (Delegate handler in Changed?.GetInvocationList() ?? [])
        {
            try
            {
                await ((Func<Task>) handler)().ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
            }
        }
    }
}

/// <summary>
/// The toast for a Critical finding that appears while somebody is watching: once per appearance, per
/// circuit.
/// </summary>
/// <remarks>
/// <para>
/// The sidebar hands this whatever its badge read on every poll. The first look at a database only takes
/// note of what is already Critical - those are on the page and in the badge, and a toast saying they just
/// appeared would be false - and every later look toasts each Critical that was not there the time before.
/// One that clears and comes back is a new appearance and toasts again; one that stays toasts once. A poll
/// that knows nothing (no recent sweep) changes nothing, because "no information" is not "it went away".
/// Muted findings are not in the glance, so they never toast.
/// </para>
/// <para>Scoped: a toast is one circuit's.</para>
/// </remarks>
internal sealed class HealthAlerts
{
    private readonly ToastService toasts;
    private string? watching;
    private HashSet<string> present = new(StringComparer.Ordinal);

    public HealthAlerts(ToastService toasts) => this.toasts = toasts;

    /// <summary>Takes note of one poll, and toasts what is newly Critical.</summary>
    /// <param name="scope">The scope the glance is about.</param>
    /// <param name="glance">What the badge read, or <see langword="null" /> when it knew nothing.</param>
    public async Task ObserveAsync(StudioScope? scope, HealthGlance? glance)
    {
        if (scope is null || glance is null)
        {
            return;
        }

        string key = scope.StoreKey + "|" + scope.DatabaseId;
        HashSet<string> now = new(glance.Criticals.Select(static x => x.MuteKey), StringComparer.Ordinal);

        if (!string.Equals(key, watching, StringComparison.OrdinalIgnoreCase))
        {
            watching = key;
            present = now;
            return;
        }

        foreach (HealthGlanceItem critical in glance.Criticals)
        {
            if (!present.Contains(critical.MuteKey))
            {
                await toasts.Error("Health: " + critical.Title + " - see the Health page.").ConfigureAwait(false);
            }
        }

        present = now;
    }
}
