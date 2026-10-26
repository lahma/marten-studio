using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using PostgresStudio.Services.Live;
using PostgresStudio.Services.Settings;

using Microsoft.AspNetCore.DataProtection;
using Microsoft.JSInterop;

namespace PostgresStudio.Services.Health;

/// <summary>
/// The secret a browser's mute hashes are keyed with (wave 6, SEC2): one random key per browser, kept by the browser only
/// in the form the host's ASP.NET Core Data Protection protects it in, under <see cref="Purpose" />.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a key at all.</b> A finding's mute key can name what its gate guards - a tenant, a stream, a shard - and the
/// browser's <c>localStorage</c> is readable by every script on the host's origin and by the next person at a shared
/// machine. A plain SHA-256 took the names off the list, but anybody who could guess a whole key could hash the guess and
/// confirm it. An HMAC under a key the browser holds only protected cannot be computed without the host's Data Protection
/// keys, so the list confirms nothing.
/// </para>
/// <para>
/// <b>Why one per browser, protected, and not one per process.</b> Data Protection does not protect deterministically -
/// every <c>Protect</c> draws a random key modifier and a random IV (<c>ManagedAuthenticatedEncryptor</c>, read at
/// ASP.NET Core 10.0.13) - so a key derived from a protected constant would be a new key at every start of the process,
/// and every mute would be lost at every restart. The browser's own random key, kept protected beside its hashes, opens
/// in every process that shares the host's key ring: mutes then survive a restart, and a deployment of several instances,
/// for exactly as long as the host's Data Protection keys do. The protector itself is created once per process.
/// </para>
/// <para>
/// <b>No host behaviour changes.</b> The provider is the host's, from its container - which every studio host has, because
/// the studio's own <c>AddRazorComponents()</c> calls <c>AddAntiforgery()</c>, and its <c>AddInteractiveServerComponents()</c>
/// calls <c>AddServerSideBlazor()</c>, and both call <c>AddDataProtection()</c> (read at ASP.NET Core 10.0.13); Blazor
/// protects every prerendered component with the same key ring already. The studio registers nothing itself: a container
/// with no <see cref="IDataProtectionProvider" /> - a hand-built one - gets an in-memory, per-process
/// <see cref="EphemeralDataProtectionProvider" /> instead, constructed here and registered nowhere, and its mutes then do
/// not survive a restart.
/// </para>
/// </remarks>
internal sealed class HealthMuteKeys
{
    /// <summary>The Data Protection purpose a browser's key is protected under: fixed, so every process of the host opens it.</summary>
    internal const string Purpose = "PostgresStudio.Health.MuteKeys.v1";

    /// <summary>How long a browser's key is: 256 bits, HMAC-SHA256's own block-sized secret.</summary>
    internal const int KeyLength = 32;

    private readonly IDataProtector protector;

    /// <summary>The keys for the host's Data Protection, or - with none in the container - an in-memory, per-process provider's.</summary>
    /// <param name="provider">The host's <see cref="IDataProtectionProvider" />, or <see langword="null" /> for none.</param>
    public HealthMuteKeys(IDataProtectionProvider? provider)
    {
        UsesHostDataProtection = provider is not null;
        protector = (provider ?? new EphemeralDataProtectionProvider()).CreateProtector(Purpose);
    }

    /// <summary>
    /// The per-process keys every circuit that was built without the container's - a hand-built <see cref="HealthMutes" />, as
    /// a test makes one - shares: an in-memory provider, so their mutes last as long as the process.
    /// </summary>
    internal static HealthMuteKeys InProcess { get; } = new(null);

    /// <summary>Whether the browser's key is protected by the host's Data Protection, rather than by an in-memory provider.</summary>
    public bool UsesHostDataProtection { get; }

    /// <summary>A new random key for a browser that has none, with the form the browser keeps it in.</summary>
    public (byte[] Key, string Protected) Create()
    {
        byte[] key = RandomNumberGenerator.GetBytes(KeyLength);
        return (key, Convert.ToBase64String(protector.Protect(key)));
    }

    /// <summary>
    /// The browser's key from the form it keeps, or <see langword="null" /> when there is none, or it is not one this host's
    /// Data Protection can open - another key ring, keys since lost, a value somebody edited.
    /// </summary>
    /// <param name="protectedKey">What the browser kept.</param>
    public byte[]? Open(string? protectedKey)
    {
        if (string.IsNullOrWhiteSpace(protectedKey))
        {
            return null;
        }

        try
        {
            byte[] key = protector.Unprotect(Convert.FromBase64String(protectedKey));
            return key.Length == KeyLength ? key : null;
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException)
        {
            return null;
        }
    }

    /// <summary>What the browser keeps for <paramref name="muteKey" />: its HMAC-SHA256 under the browser's key, as lowercase hex.</summary>
    /// <param name="key">The browser's key.</param>
    /// <param name="muteKey">The finding's mute key.</param>
    public static string Hash(ReadOnlySpan<byte> key, string muteKey)
    {
        ArgumentNullException.ThrowIfNull(muteKey);
        return Convert.ToHexStringLower(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(muteKey)));
    }
}

/// <summary>
/// The findings this browser has muted: kept in the browser's <c>localStorage</c> as keyed hashes, read once per circuit.
/// </summary>
/// <remarks>
/// <para>
/// <b>Per visitor, and nowhere else.</b> A mute is one person's "I know about this one", so it lives in their
/// browser (<c>martenStudio.health</c> in <c>js/studio.js</c>) and the studio writes nothing to the
/// host's database or to any other visitor's view. A mute key is the source, the database and the finding's
/// key, so muting a finding about one database leaves the same code on another alone.
/// </para>
/// <para>
/// <b>Hashed, under a key the browser cannot use, before it leaves the circuit.</b> A finding's key can carry what its gate
/// guards - E03's subject is <c>tenant/streamName</c> - and <c>localStorage</c> is readable by every script on the host's
/// origin and by the next person at a shared machine. So the browser is handed, and keeps, only the HMAC-SHA256 of each key
/// under its own random key (<see cref="HealthMuteKeys" />), as lowercase hex, beside that key in the form the host's Data
/// Protection protects it in; a key is muted when its hash is in the list, and no raw key - mute key or HMAC key - reaches a
/// JS call. Nobody who reads the list can confirm a guess at a key without the host's Data Protection keys.
/// </para>
/// <para>
/// <b>Correct without it.</b> A private window, blocked storage, a prerender or a closed tab all answer as an
/// empty list, and every access is behind <see cref="StudioLiveUpdates.IsInteropUnavailable" /> on this side
/// and a <c>try</c> on the browser's: what that costs is that nothing is muted, never a broken page. A key this instance
/// cannot open - another instance's key ring, or this host's own since lost - is the same here: nothing muted, and the list
/// left for the key ring that made it until the browser next mutes something.
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

    /// <summary>How long a stored entry is: an HMAC-SHA256, in hex.</summary>
    private const int HashLength = 64;

    private readonly HealthMuteKeys keys;
    private readonly List<string> hashes = [];
    private readonly Lock gate = new();
    private byte[]? key;
    private string? protectedKey;
    private bool loaded;
    private bool unreadable;

    /// <summary>A circuit's mutes, keyed through the host's Data Protection.</summary>
    /// <param name="keys">
    /// The process's mute keys, as the studio's registration makes them - or <see langword="null" />, for a circuit built by
    /// hand as a test builds one, to key through the in-process provider (<see cref="HealthMuteKeys.InProcess" />).
    /// </param>
    public HealthMutes(HealthMuteKeys? keys = null) => this.keys = keys ?? HealthMuteKeys.InProcess;

    /// <summary>Raised after the list changed, so the sidebar's badge and the page both redraw.</summary>
    public event Func<Task>? Changed;

    /// <summary>Whether the browser's list has been read yet.</summary>
    public bool IsLoaded => loaded;

    /// <summary>
    /// Whether the browser keeps a list under a key this instance's key ring cannot open, and has muted nothing since - so
    /// nothing reads as muted here, and the list is left for the key ring that made it.
    /// </summary>
    internal bool IsUnreadableHere
    {
        get
        {
            lock (gate)
            {
                return unreadable;
            }
        }
    }

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

    /// <summary>
    /// What the browser keeps for <paramref name="muteKey" />: its HMAC under this browser's key, or <see langword="null" />
    /// before the browser's key is known.
    /// </summary>
    internal string? Hash(string muteKey)
    {
        ArgumentNullException.ThrowIfNull(muteKey);

        lock (gate)
        {
            return key is null ? null : HealthMuteKeys.Hash(key, muteKey);
        }
    }

    /// <summary>Whether <paramref name="muteKey" /> is muted in this browser.</summary>
    public bool IsMuted(string muteKey)
    {
        ArgumentNullException.ThrowIfNull(muteKey);

        lock (gate)
        {
            return key is not null && hashes.Contains(HealthMuteKeys.Hash(key, muteKey), StringComparer.Ordinal);
        }
    }

    /// <summary>Reads the browser's list, once per circuit. A browser that cannot answer has muted nothing.</summary>
    /// <remarks>
    /// <para>
    /// Anything stored that no key ring made - a list from before wave 6 (plain SHA-256 hashes, or the raw keys a
    /// pre-release 0.4 build stored), a value that is not the studio's - is written back at once as a new key and an empty
    /// list, so nothing stale waits in <c>localStorage</c> for the next mute to overwrite it. Those mutes are dropped, not
    /// carried over: nothing is migrated.
    /// </para>
    /// <para>
    /// A well-formed key this instance cannot open is not (W6-fix): on a host whose instances do not share a key ring, the
    /// next request may reach the instance that made it, and writing over it here would lose every mute on every change of
    /// instance. Its list reads as nothing muted here (<see cref="IsUnreadableHere" />) and stays as it is until this browser
    /// mutes something, which replaces it with a key of this instance's.
    /// </para>
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

        StoredMutes parsed = Parse(stored, out bool dropped);
        byte[]? opened = keys.Open(parsed.Key);
        string? cleaned = null;

        lock (gate)
        {
            hashes.Clear();

            if (opened is not null)
            {
                key = opened;
                protectedKey = parsed.Key;
                unreadable = false;
                hashes.AddRange(parsed.Muted?.OfType<string>() ?? []);
            }
            else if (IsWellFormedKey(parsed.Key))
            {
                // A key this instance cannot open, but one a key ring could have made: another instance's, on a host whose
                // instances do not share a key ring, or this host's before it lost its keys. Its list is unreadable here and
                // is left as it is - the instance that made it may answer the next request, and would read it. Nothing is
                // muted here until this browser mutes something, which starts a key of this instance's (SetAsync).
                (key, protectedKey) = (null, null);
                unreadable = true;
                dropped = false;
            }
            else
            {
                // No key, or not one at all: whatever hashes came with it are nothing a key ring made.
                (key, protectedKey) = keys.Create();
                unreadable = false;
                dropped |= stored is { Length: > 0 };
            }

            loaded = true;

            if (dropped)
            {
                cleaned = SerializeLocked();
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

        // The browser's key first: a mute set before the list was read would otherwise start a key of its own and write
        // over whatever the browser already kept.
        await LoadAsync(jsRuntime, cancellationToken).ConfigureAwait(false);

        string json;

        lock (gate)
        {
            if (key is null)
            {
                if (!muted)
                {
                    // An unreadable list has nothing muted here to unmute, and is still not this instance's to replace.
                    return;
                }

                // The browser's first mute since its list became unreadable here: the list is replaced now, and only now,
                // with a key this instance can open - the mutes another key ring keyed are lost at this point, not before.
                (key, protectedKey) = keys.Create();
                hashes.Clear();
                unreadable = false;
            }

            string hash = HealthMuteKeys.Hash(key, muteKey);
            hashes.RemoveAll(x => string.Equals(x, hash, StringComparison.Ordinal));

            if (muted)
            {
                hashes.Add(hash);

                while (hashes.Count > MaxKeys)
                {
                    hashes.RemoveAt(0);
                }
            }

            json = SerializeLocked();
        }

        await WriteAsync(jsRuntime, json, cancellationToken).ConfigureAwait(false);
        await RaiseChangedAsync().ConfigureAwait(false);
    }

    /// <summary>What the browser keeps: the protected key and the hashes, as JSON. Called under <see cref="gate" />.</summary>
    private string SerializeLocked() => JsonSerializer.Serialize(new StoredMutes(protectedKey, [.. hashes]));

    /// <summary>Hands the browser what to keep. A browser that cannot keep it simply forgets.</summary>
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

    /// <summary>What the browser stored, or nothing for anything that is not the studio's.</summary>
    internal static StoredMutes Parse(string? stored) => Parse(stored, out _);

    /// <summary>
    /// What the browser stored - its protected key and its hashes, newest <see cref="MaxKeys" /> - and whether anything
    /// stored was left out: an entry that is not an HMAC-SHA256 in lowercase hex, a duplicate, one past the bound, or a value
    /// that is not the studio's object at all (a list from before wave 6 among them, dropped rather than migrated).
    /// </summary>
    internal static StoredMutes Parse(string? stored, out bool dropped)
    {
        dropped = false;

        if (string.IsNullOrWhiteSpace(stored))
        {
            return StoredMutes.None;
        }

        StoredMutes? parsed;

        try
        {
            parsed = JsonSerializer.Deserialize<StoredMutes>(stored);
        }
        catch (JsonException)
        {
            dropped = true;
            return StoredMutes.None;
        }

        if (parsed is null)
        {
            dropped = true;
            return StoredMutes.None;
        }

        string?[] muted = parsed.Muted ?? [];
        List<string> kept = [.. muted.OfType<string>().Where(IsHash).Distinct(StringComparer.Ordinal).TakeLast(MaxKeys)];
        dropped = kept.Count != muted.Length || parsed.Muted is null;
        return new StoredMutes(parsed.Key, [.. kept]);
    }

    /// <summary>
    /// Whether <paramref name="protectedKey" /> could be a key some key ring protected - base64 of a payload at least as long as
    /// the key it carries - rather than nothing, or a value that is not one at all.
    /// </summary>
    private static bool IsWellFormedKey(string? protectedKey)
    {
        if (string.IsNullOrWhiteSpace(protectedKey))
        {
            return false;
        }

        byte[] buffer = new byte[(protectedKey.Length * 3 / 4) + 3];
        return Convert.TryFromBase64String(protectedKey, buffer, out int written) && written > HealthMuteKeys.KeyLength;
    }

    /// <summary>Whether <paramref name="entry" /> has the shape <see cref="HealthMuteKeys.Hash" /> gives: 64 lowercase hex digits.</summary>
    private static bool IsHash(string entry) =>
        entry.Length == HashLength && entry.All(static c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    /// <summary>What <c>localStorage</c> keeps: the browser's key as the host's Data Protection protected it, and the hashes.</summary>
    /// <param name="Key">The protected key, base64.</param>
    /// <param name="Muted">The hashes, oldest first.</param>
    internal sealed record StoredMutes(
        [property: JsonPropertyName("key")] string? Key,
        [property: JsonPropertyName("muted")] string?[]? Muted)
    {
        /// <summary>Nothing stored.</summary>
        public static StoredMutes None { get; } = new(null, []);
    }

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
