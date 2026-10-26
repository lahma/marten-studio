using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Bunit;

using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using PostgresStudio.Services.Health;

namespace MartenStudio.Tests.Health;

/// <summary>
/// The mutes that live in the browser and nowhere else (H1), keyed since wave 6 (SEC2): each browser's hashes are an
/// HMAC-SHA256 under a random key of its own, which the browser keeps only as the host's Data Protection protected it - so
/// a script that reads <c>localStorage</c>, or the next person at a shared machine, can no longer confirm a guessed key.
/// </summary>
public class HealthMutesTests
{
    private static CancellationToken Token => Xunit.TestContext.Current.CancellationToken;

    /// <summary>A browser whose <c>localStorage</c> holds <paramref name="stored" />, and that keeps what it is handed.</summary>
    private static BunitContext Browser(string? stored)
    {
        BunitContext context = new();
        context.JSInterop.Setup<string?>("martenStudio.health.getMuted").SetResult(stored);
        context.JSInterop.SetupVoid("martenStudio.health.setMuted", _ => true).SetVoidResult();
        return context;
    }

    /// <summary>The last thing the circuit handed the browser to keep.</summary>
    private static string LastWritten(BunitContext context) =>
        (string) context.JSInterop.Invocations.Last(static x => x.Identifier == "martenStudio.health.setMuted").Arguments.Single()!;

    /// <summary>A mute key, set in a fresh browser through <paramref name="keys" />, and what the browser then kept.</summary>
    private static async Task<string> MutedAsync(HealthMuteKeys keys, params string[] muteKeys)
    {
        using BunitContext context = Browser(null);
        HealthMutes mutes = new(keys);
        await mutes.LoadAsync(context.JSInterop.JSRuntime, Token);

        foreach (string muteKey in muteKeys)
        {
            await mutes.SetAsync(context.JSInterop.JSRuntime, muteKey, muted: true, Token);
        }

        return LastWritten(context);
    }

    /// <summary>The circuit that next reads <paramref name="stored" /> through <paramref name="keys" />.</summary>
    private static async Task<(HealthMutes Mutes, BunitContext Browser)> ReadBackAsync(HealthMuteKeys keys, string? stored)
    {
        BunitContext context = Browser(stored);
        HealthMutes mutes = new(keys);
        await mutes.LoadAsync(context.JSInterop.JSRuntime, Token);
        return (mutes, context);
    }

    private static bool WroteBack(BunitContext context) =>
        context.JSInterop.Invocations.Any(static x => x.Identifier == "martenStudio.health.setMuted");

    [Fact]
    public async Task A_mute_is_written_to_the_browser_and_read_back_by_the_next_circuit()
    {
        HealthMuteKeys keys = new(new EphemeralDataProtectionProvider());
        string written = await MutedAsync(keys, "default|db|K02", "default|db|K03");

        (HealthMutes after, BunitContext browser) = await ReadBackAsync(keys, written);
        using (browser)
        {
            after.IsMuted("default|db|K02").Should().BeTrue();
            after.IsMuted("default|db|K03").Should().BeTrue();
            after.IsMuted("default|other|K02").Should().BeFalse("a mute is about one database");
            WroteBack(browser).Should().BeFalse("what this host's key ring protected is read as it is, and not written back");

            await after.SetAsync(browser.JSInterop.JSRuntime, "default|db|K02", muted: false, Token);
            after.Keys.Should().Equal(after.Hash("default|db|K03"));
            after.IsMuted("default|db|K02").Should().BeFalse();
        }
    }

    /// <summary>
    /// SEC2 L1, and wave 6: a mute key can carry what its finding's gate guards - E03's subject is <c>tenant/streamName</c> -
    /// and <c>localStorage</c> is readable by every script on the host's origin. The browser is handed keyed hashes and a
    /// protected key only: nothing it holds names the key, and nothing it holds lets a reader hash a guess.
    /// </summary>
    [Fact]
    public async Task No_raw_key_and_no_usable_hash_reaches_the_browser()
    {
        const string Key = "default|tenants.acme_db|E03|acme/orders-4711";
        HealthMuteKeys keys = new(new EphemeralDataProtectionProvider());

        string written = await MutedAsync(keys, Key, "default|tenants.acme_db|K02");

        written.Should().NotContain("acme").And.NotContain("orders-4711").And.NotContain("|").And.NotContain("E03");

        using JsonDocument stored = JsonDocument.Parse(written);
        string[] muted = [.. stored.RootElement.GetProperty("muted").EnumerateArray().Select(static x => x.GetString()!)];
        string protectedKey = stored.RootElement.GetProperty("key").GetString()!;

        muted.Should().HaveCount(2).And.OnlyContain(static x => x.Length == 64 && x.All(static c => char.IsAsciiHexDigit(c) && !char.IsAsciiLetterUpper(c)),
            "each entry is an HMAC-SHA256 in lowercase hex");
        muted.Should().NotContain(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Key))),
            "a reader who guesses the whole key and hashes the guess finds nothing to compare it with");

        byte[] browserKey = keys.Open(protectedKey)!;
        browserKey.Should().HaveCount(HealthMuteKeys.KeyLength);
        written.Should().NotContain(Convert.ToBase64String(browserKey)).And.NotContain(Convert.ToHexStringLower(browserKey),
            "the browser keeps its key only as the host's Data Protection protected it");
        muted.Should().Contain(HealthMuteKeys.Hash(browserKey, Key), "and the hash is the HMAC under that key");
    }

    [Fact]
    public async Task The_same_key_gives_the_same_HMAC_within_a_process()
    {
        HealthMuteKeys keys = new(new EphemeralDataProtectionProvider());
        string written = await MutedAsync(keys, "default|db|E03|acme/orders-4711");

        (HealthMutes first, BunitContext one) = await ReadBackAsync(keys, written);
        (HealthMutes second, BunitContext two) = await ReadBackAsync(keys, written);
        using (one)
        using (two)
        {
            first.Hash("default|db|E03|acme/orders-4711").Should().Be(second.Hash("default|db|E03|acme/orders-4711"))
                .And.Be(first.Keys.Single(), "two tabs of one browser, in one process, hash a key alike");
            second.IsMuted("default|db|E03|acme/orders-4711").Should().BeTrue();
            first.Hash("default|db|E03|acme/orders").Should().NotBe(first.Hash("default|db|E03|globex/orders"));
        }

        byte[] key = RandomNumberGenerator.GetBytes(HealthMuteKeys.KeyLength);
        HealthMuteKeys.Hash(key, "default|db|K02").Should().Be(HealthMuteKeys.Hash(key, "default|db|K02"))
            .And.Be(Convert.ToHexStringLower(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes("default|db|K02"))), "an HMAC-SHA256 over the key's UTF-8");
    }

    /// <summary>
    /// Another provider - another instance's key ring, or a host that lost its keys - cannot open what this one protected: it
    /// reads nothing as muted, and leaves the list as it is until the browser mutes something there, which replaces it with a
    /// key of its own whose HMAC of the same mute key is another one.
    /// </summary>
    [Fact]
    public async Task A_different_provider_gives_a_different_HMAC_and_replaces_the_list_only_on_a_mute()
    {
        const string Key = "default|db|K02";
        HealthMuteKeys ours = new(new EphemeralDataProtectionProvider());
        HealthMuteKeys theirs = new(new EphemeralDataProtectionProvider());

        string written = await MutedAsync(ours, Key);
        string ourHash = JsonDocument.Parse(written).RootElement.GetProperty("muted")[0].GetString()!;

        (HealthMutes elsewhere, BunitContext browser) = await ReadBackAsync(theirs, written);
        using (browser)
        {
            elsewhere.IsMuted(Key).Should().BeFalse("another key ring cannot open this browser's key");
            elsewhere.IsUnreadableHere.Should().BeTrue();
            WroteBack(browser).Should().BeFalse("a list another key ring made is left for it, not written over on a read");

            await elsewhere.SetAsync(browser.JSInterop.JSRuntime, Key, muted: true, Token);

            using JsonDocument rewritten = JsonDocument.Parse(LastWritten(browser));
            string theirHash = rewritten.RootElement.GetProperty("muted").EnumerateArray().Single().GetString()!;
            theirHash.Should().NotBe(ourHash, "the key it starts with hashes the same mute key differently");
            theirs.Open(rewritten.RootElement.GetProperty("key").GetString()).Should().NotBeNull("with a key this instance can open");
            ours.Open(rewritten.RootElement.GetProperty("key").GetString()).Should().BeNull();
            elsewhere.IsUnreadableHere.Should().BeFalse();
            elsewhere.IsMuted(Key).Should().BeTrue();
        }
    }

    /// <summary>
    /// W6-fix: on a host whose instances do not share a key ring, a visit to another instance used to write over the
    /// browser's list at once and lose every mute. Now the list survives the visit: the instance that made it still reads it.
    /// An unmute there changes nothing either, because nothing reads as muted there to unmute.
    /// </summary>
    [Fact]
    public async Task A_visit_to_an_instance_with_another_key_ring_keeps_every_mute_for_the_instance_that_made_them()
    {
        const string Key = "default|db|K02";
        HealthMuteKeys first = new(new EphemeralDataProtectionProvider());
        HealthMuteKeys second = new(new EphemeralDataProtectionProvider());

        string written = await MutedAsync(first, Key, "default|db|K03");

        (HealthMutes onSecond, BunitContext browser) = await ReadBackAsync(second, written);
        using (browser)
        {
            onSecond.IsMuted(Key).Should().BeFalse();
            await onSecond.SetAsync(browser.JSInterop.JSRuntime, Key, muted: false, Token);
            WroteBack(browser).Should().BeFalse("neither the read nor an unmute replaces a list this instance cannot read");
        }

        (HealthMutes backOnFirst, BunitContext again) = await ReadBackAsync(first, written);
        using (again)
        {
            backOnFirst.IsMuted(Key).Should().BeTrue("the list the browser still holds is the first instance's, as it made it");
            backOnFirst.IsMuted("default|db|K03").Should().BeTrue();
        }
    }

    /// <summary>A stored key that is not one at all - not base64, or too short to carry a key - is replaced at once.</summary>
    [Theory]
    [InlineData("not base64 at all!")]
    [InlineData("AAAA")]
    public async Task A_key_that_no_key_ring_could_have_made_is_replaced_at_once(string notAKey)
    {
        HealthMuteKeys keys = new(new EphemeralDataProtectionProvider());
        string stored = JsonSerializer.Serialize(new HealthMutes.StoredMutes(notAKey, [new string('a', 64)]));

        (HealthMutes mutes, BunitContext browser) = await ReadBackAsync(keys, stored);
        using (browser)
        {
            mutes.IsUnreadableHere.Should().BeFalse();
            using JsonDocument rewritten = JsonDocument.Parse(LastWritten(browser));
            rewritten.RootElement.GetProperty("muted").GetArrayLength().Should().Be(0);
            keys.Open(rewritten.RootElement.GetProperty("key").GetString()).Should().NotBeNull();
        }
    }

    /// <summary>
    /// A container with no Data Protection - a hand-built one - keys through an in-memory provider the studio constructs and
    /// registers nowhere: mutes hold for the process, and a restart of it (a new provider) starts them again.
    /// </summary>
    [Fact]
    public async Task Without_Data_Protection_the_keys_are_the_process_own_and_do_not_survive_a_restart()
    {
        ServiceCollection bare = new();
        using ServiceProvider provider = bare.BuildServiceProvider();
        HealthMuteKeys fallback = new(provider.GetService<IDataProtectionProvider>());

        fallback.UsesHostDataProtection.Should().BeFalse();
        provider.GetService<IDataProtectionProvider>().Should().BeNull("the fallback registers nothing on the host's behalf");

        string written = await MutedAsync(fallback, "default|db|K02");
        (HealthMutes sameProcess, BunitContext one) = await ReadBackAsync(fallback, written);
        (HealthMutes restarted, BunitContext two) = await ReadBackAsync(new HealthMuteKeys(null), written);
        using (one)
        using (two)
        {
            sameProcess.IsMuted("default|db|K02").Should().BeTrue("within the process the in-memory key ring opens it");
            restarted.IsMuted("default|db|K02").Should().BeFalse("a restart's new in-memory key ring cannot");
        }

        new HealthMutes().IsMuted("default|db|K02").Should().BeFalse("a hand-built circuit keys through the in-process provider, and has read nothing");
        HealthMuteKeys.InProcess.UsesHostDataProtection.Should().BeFalse();
    }

    /// <summary>
    /// Every studio host has Data Protection already - registered by ASP.NET Core's own <c>AddRazorComponents()</c> and
    /// <c>AddInteractiveServerComponents()</c>, which the studio's registration calls - so its mute keys are the host's, and
    /// the studio adds no Data Protection registration of its own.
    /// </summary>
    [Fact]
    public void A_studio_host_keys_through_the_Data_Protection_its_Blazor_registration_brought()
    {
        ServiceCollection blazor = new();
        blazor.AddRazorComponents().AddInteractiveServerComponents();

        ServiceCollection studio = new();
        studio.AddLogging();
        studio.AddMartenStudio();

        static string[] DataProtection(IServiceCollection services) =>
        [
            .. services
                .Where(static x => x.ServiceType.Namespace?.StartsWith("Microsoft.AspNetCore.DataProtection", StringComparison.Ordinal) == true)
                .Select(static x => x.ServiceType.FullName + " <- " + (x.ImplementationType?.FullName ?? "a factory or an instance"))
                .Order(StringComparer.Ordinal),
        ];

        DataProtection(blazor).Should().NotBeEmpty("AddRazorComponents calls AddAntiforgery, and both call AddDataProtection");
        DataProtection(studio).Should().Equal(DataProtection(blazor), "the studio registers no Data Protection beyond what Blazor's own registration does");

        using ServiceProvider provider = studio.BuildServiceProvider();
        HealthMuteKeys keys = provider.GetRequiredService<HealthMuteKeys>();

        keys.UsesHostDataProtection.Should().BeTrue();
        provider.GetRequiredService<HealthMuteKeys>().Should().BeSameAs(keys, "the protector is created once per process");
    }

    /// <summary>
    /// Wave 6 resets every mute once: a list from before it - plain SHA-256 hashes, or the raw keys a pre-release 0.4 build
    /// stored - is dropped, not migrated, and written over at once with a new key and an empty list.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_list_from_before_wave_6_is_dropped_and_written_over_at_once(bool rawKeys)
    {
        string entry = rawKeys
            ? "default|db|E03|acme/orders-4711"
            : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("default|db|K03")));
        string old = JsonSerializer.Serialize(new List<string> { entry });

        HealthMuteKeys keys = new(new EphemeralDataProtectionProvider());
        (HealthMutes mutes, BunitContext browser) = await ReadBackAsync(keys, old);
        using (browser)
        {
            mutes.IsMuted("default|db|K03").Should().BeFalse("a plain hash is not carried over");
            mutes.IsMuted("default|db|E03|acme/orders-4711").Should().BeFalse();

            string rewritten = LastWritten(browser);
            rewritten.Should().NotContain("acme").And.NotContain(JsonSerializer.Deserialize<string[]>(old)![0]);
            Parse(rewritten).Muted.Should().BeEmpty();
            keys.Open(Parse(rewritten).Key).Should().NotBeNull();
        }
    }

    private static HealthMutes.StoredMutes Parse(string stored) => HealthMutes.Parse(stored);

    [Fact]
    public async Task A_first_visit_writes_nothing_until_something_is_muted()
    {
        (HealthMutes mutes, BunitContext browser) = await ReadBackAsync(new HealthMuteKeys(new EphemeralDataProtectionProvider()), null);
        using (browser)
        {
            mutes.IsLoaded.Should().BeTrue();
            mutes.Keys.Should().BeEmpty();
            WroteBack(browser).Should().BeFalse("nothing was stored, so there is nothing to write over");
        }
    }

    /// <summary>A mute set before the list was read reads the list first, and keeps the browser's own key and mutes.</summary>
    [Fact]
    public async Task A_mute_set_before_the_list_was_read_keeps_what_the_browser_had()
    {
        HealthMuteKeys keys = new(new EphemeralDataProtectionProvider());
        string written = await MutedAsync(keys, "default|db|K02");

        using BunitContext browser = Browser(written);
        HealthMutes mutes = new(keys);
        await mutes.SetAsync(browser.JSInterop.JSRuntime, "default|db|K03", muted: true, Token);

        mutes.IsMuted("default|db|K02").Should().BeTrue();
        mutes.IsMuted("default|db|K03").Should().BeTrue();
        Parse(LastWritten(browser)).Key.Should().Be(Parse(written).Key, "the browser's own key, not a second one");
    }

    /// <summary>Blocked storage, a private window, a prerender: nothing is muted, and nothing breaks.</summary>
    [Fact]
    public async Task A_browser_that_cannot_answer_has_muted_nothing()
    {
        using BunitContext context = new();
        context.JSInterop.Setup<string?>("martenStudio.health.getMuted").SetException(new Microsoft.JSInterop.JSException("storage is blocked"));
        context.JSInterop.SetupVoid("martenStudio.health.setMuted", _ => true).SetException(new Microsoft.JSInterop.JSException("storage is blocked"));

        HealthMutes mutes = new();
        await mutes.LoadAsync(context.JSInterop.JSRuntime, Token);

        mutes.IsLoaded.Should().BeTrue();
        mutes.Keys.Should().BeEmpty();

        await mutes.SetAsync(context.JSInterop.JSRuntime, "default|db|K02", muted: true, Token);
        mutes.IsMuted("default|db|K02").Should().BeTrue("the mute holds for this circuit even when the browser cannot keep it");
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("not json", true)]
    [InlineData("[\"default|db|K02\"]", true)]
    [InlineData("{\"a\":1}", true)]
    [InlineData("{\"key\":\"x\",\"muted\":[\"\",null]}", true)]
    [InlineData("{\"key\":\"x\",\"muted\":[\"default|db|K02\"]}", true)]
    [InlineData("{\"key\":\"x\",\"muted\":[\"8B2C9E0F8B2C9E0F8B2C9E0F8B2C9E0F8B2C9E0F8B2C9E0F8B2C9E0F8B2C9E0F\"]}", true)]
    public void Anything_stored_that_is_not_the_studios_list_of_hashes_holds_none(string? stored, bool dropped)
    {
        HealthMutes.StoredMutes parsed = HealthMutes.Parse(stored, out bool left);

        parsed.Muted.Should().BeEmpty();
        left.Should().Be(dropped);
    }

    [Fact]
    public void A_stored_list_keeps_its_newest_two_hundred()
    {
        byte[] key = RandomNumberGenerator.GetBytes(HealthMuteKeys.KeyLength);
        string json = JsonSerializer.Serialize(new HealthMutes.StoredMutes("x", [.. Enumerable.Range(0, 250).Select(i => HealthMuteKeys.Hash(key, "k" + i))]));

        HealthMutes.StoredMutes parsed = HealthMutes.Parse(json, out bool dropped);

        parsed.Muted.Should().HaveCount(HealthMutes.MaxKeys);
        parsed.Muted![0].Should().Be(HealthMuteKeys.Hash(key, "k50"));
        parsed.Key.Should().Be("x");
        dropped.Should().BeTrue("the oldest fifty were left out, so the list is written back");
    }

    [Fact]
    public void A_key_that_is_not_one_this_host_protected_opens_to_nothing()
    {
        HealthMuteKeys keys = new(new EphemeralDataProtectionProvider());

        keys.Open(null).Should().BeNull();
        keys.Open("").Should().BeNull();
        keys.Open("not base64 at all!").Should().BeNull();
        keys.Open(Convert.ToBase64String(RandomNumberGenerator.GetBytes(64))).Should().BeNull("a value somebody edited is no key");
        keys.Open(Convert.ToBase64String(new EphemeralDataProtectionProvider().CreateProtector(HealthMuteKeys.Purpose).Protect(new byte[32])))
            .Should().BeNull("another key ring's, even under the same purpose");
        keys.Open(Convert.ToBase64String(new EphemeralDataProtectionProvider().CreateProtector("another purpose").Protect(new byte[32])))
            .Should().BeNull();

        (byte[] key, string protectedKey) = keys.Create();
        keys.Open(protectedKey).Should().Equal(key);
        keys.Create().Protected.Should().NotBe(protectedKey, "every browser gets a key of its own");
    }
}
