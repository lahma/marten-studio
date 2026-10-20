using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using PostgresStudio.Services.Live;
using PostgresStudio.Services.Settings;

using Microsoft.JSInterop;

namespace PostgresStudio.Services.Health;

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
/// The toast for a Critical finding that appears while somebody is watching: once per circuit, however often it comes
/// and goes inside a cool-down.
/// </summary>
/// <remarks>
/// <para>
/// The sidebar hands this whatever its badge read on every poll. The first look at a database only takes note of what
/// is already Critical - those are on the page and in the badge, and a toast saying they just appeared would be false -
/// and every later look toasts each Critical that was not there the time before, unless this circuit has seen that
/// finding Critical within the last <see cref="CoolDown" />. One that stays toasts once. One that clears and comes back
/// toasts again only once it has stayed away for a whole cool-down, measured from the last sweep that found it to the
/// sweep that finds it again: a check that sits at its threshold - C03 at about 95% of connections, a Warning one sweep
/// and Critical the next - would otherwise toast every open tab every other sweep for as long as the usage hovers there.
/// "Seen" includes the first look: a finding that was already Critical when the tab arrived and then flaps is the same
/// flap, and the visitor already knows about it. Another finding - another code, or the same code about another subject
/// - is in nobody else's cool-down and toasts at once. A poll that knows nothing (no recent sweep) changes nothing,
/// because "no information" is not "it went away". Muted findings are not in the glance, so they never toast.
/// </para>
/// <para>
/// "The same finding" is its mute key - store, database, code and subject - which is what the glance, the mutes and the
/// badge already know it by across sweeps (<see cref="HealthGlanceItem.MuteKey" />). The cool-down is the circuit's, not
/// the database's watch: leaving for another database and coming back does not start it again. Time is the sweeps' own,
/// <see cref="HealthGlance.MeasuredAt" />: a poll that reads the same sweep again moves nothing, and nothing here reads
/// a clock.
/// </para>
/// <para>
/// <b>A glance read for another scope is nothing.</b> The sidebar's poll runs on the thread pool and a scope change on
/// the renderer, so a read that was in flight when the visitor moved from database A to B can arrive after the move.
/// Taken as B's first look it would note A's Criticals as B's, and B's next real glance would toast B's existing
/// Criticals as new; taken as A's it could toast something about A while the visitor reads B. So a glance whose
/// <see cref="HealthGlance.Scope" /> - stamped by the read with the scope it was read for - is not the scope being
/// observed changes nothing, exactly like a poll that knows nothing, and B's first real glance is B's first look.
/// The check is here rather than only at the call site because this is the state the race corrupts, and every caller
/// gets it.
/// </para>
/// <para>Scoped: a toast, and its cool-down, are one circuit's.</para>
/// </remarks>
internal sealed class HealthAlerts
{
    /// <summary>The shortest cool-down, whatever the health interval: a quarter of an hour.</summary>
    internal static readonly TimeSpan MinimumCoolDown = TimeSpan.FromMinutes(15);

    /// <summary>
    /// How many health intervals the cool-down is at least. Measured from the last sweep that found it, a finding that
    /// missed one sweep has been gone two intervals, so the cool-down has to be longer than two or a long
    /// <c>HealthInterval</c> would still toast a one-sweep flap every other sweep.
    /// </summary>
    internal const int CoolDownIntervals = 3;

    private readonly ToastService toasts;
    private readonly IStudioSettings settings;
    private readonly Lock gate = new();

    /// <summary>When each Critical this circuit has seen was last in a glance, by mute key: the sweep's time.</summary>
    private readonly Dictionary<string, DateTimeOffset> lastSeen = new(StringComparer.Ordinal);

    private string? watching;
    private HashSet<string> present = new(StringComparer.Ordinal);

    public HealthAlerts(ToastService toasts, IStudioSettings settings)
    {
        ArgumentNullException.ThrowIfNull(toasts);
        ArgumentNullException.ThrowIfNull(settings);

        this.toasts = toasts;
        this.settings = settings;
    }

    /// <summary>
    /// How long a finding that was Critical must stay away before its return toasts again: a quarter of an hour, or
    /// <see cref="CoolDownIntervals" /> of the advisor's real cadence when that is longer.
    /// </summary>
    /// <remarks>
    /// A quarter of an hour because a threshold flap is a pattern of many sweeps, not one - fifteen of them at the default
    /// minute - and because nothing is lost by it: the badge and the Health page say what is Critical on every poll, and a
    /// toast only says that something new happened, which a finding seen in the last quarter of an hour has not.
    /// </remarks>
    internal TimeSpan CoolDown
    {
        get
        {
            TimeSpan intervals = settings.EffectiveHealthInterval * CoolDownIntervals;
            return intervals > MinimumCoolDown ? intervals : MinimumCoolDown;
        }
    }

    /// <summary>Takes note of one poll, and toasts what is newly Critical.</summary>
    /// <param name="scope">
    /// The scope the visitor is on as the glance arrives - the circuit's active scope now, not when the read began.
    /// </param>
    /// <param name="glance">
    /// What the badge read, or <see langword="null" /> when it knew nothing. One read for any scope but
    /// <paramref name="scope" /> is ignored.
    /// </param>
    public async Task ObserveAsync(StudioScope? scope, HealthGlance? glance)
    {
        if (scope is null || glance is null || glance.Scope != scope)
        {
            return;
        }

        string key = scope.StoreKey + "|" + scope.DatabaseId;
        HashSet<string> now = new(glance.Criticals.Select(static x => x.MuteKey), StringComparer.Ordinal);
        DateTimeOffset at = glance.MeasuredAt;
        TimeSpan coolDown = CoolDown;
        List<string> toast = [];

        // Decided and remembered under the lock, toasted after it: the sidebar's poll and a scope change can both be
        // here, and neither may toast what the other already has.
        lock (gate)
        {
            if (string.Equals(key, watching, StringComparison.OrdinalIgnoreCase))
            {
                foreach (HealthGlanceItem critical in glance.Criticals)
                {
                    bool seenLately = lastSeen.TryGetValue(critical.MuteKey, out DateTimeOffset seen) && at - seen < coolDown;

                    if (!present.Contains(critical.MuteKey) && !seenLately)
                    {
                        toast.Add(critical.Title);
                    }
                }
            }

            watching = key;
            present = now;
            Remember(now, at, coolDown);
        }

        foreach (string title in toast)
        {
            await toasts.Error("Health: " + title + " - see the Health page.").ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Notes that every key in <paramref name="now" /> was Critical at <paramref name="at" />, and forgets the keys last
    /// seen a whole cool-down ago - their return toasts whether they are remembered or not, so a long-lived circuit does
    /// not keep them.
    /// </summary>
    private void Remember(HashSet<string> now, DateTimeOffset at, TimeSpan coolDown)
    {
        foreach (string muteKey in now)
        {
            lastSeen[muteKey] = lastSeen.TryGetValue(muteKey, out DateTimeOffset seen) && seen > at ? seen : at;
        }

        foreach (string stale in lastSeen.Where(x => at - x.Value >= coolDown).Select(static x => x.Key).ToList())
        {
            lastSeen.Remove(stale);
        }
    }
}
