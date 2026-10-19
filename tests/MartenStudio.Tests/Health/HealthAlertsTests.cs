using Bunit;

using MartenStudio.Services;
using MartenStudio.Services.Health;

namespace MartenStudio.Tests.Health;

/// <summary>
/// The toast for a Critical that appears while somebody watches - once per appearance - and the mutes that
/// live in the browser and nowhere else.
/// </summary>
public class HealthAlertsTests
{
    private static readonly StudioScope Scope = new("default", "localhost.marten", null);

    private static readonly DateTimeOffset Now = new(2026, 10, 15, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken Token => Xunit.TestContext.Current.CancellationToken;

    private static HealthGlance Glance(params string[] criticals) =>
        new(Now, criticals.Length, 0, [.. criticals.Select(static x => new HealthGlanceItem("default|db|" + x, x + " is broken"))]);

    [Fact]
    public async Task What_is_already_Critical_at_the_first_look_is_not_toasted()
    {
        ToastService toasts = new();
        HealthAlerts alerts = new(toasts);

        await alerts.ObserveAsync(Scope, Glance("D01"));

        toasts.Messages.Should().BeEmpty("it is on the page and in the badge already; a toast would say it just appeared");
    }

    [Fact]
    public async Task A_Critical_that_appears_while_watching_is_toasted_once()
    {
        ToastService toasts = new();
        HealthAlerts alerts = new(toasts);

        await alerts.ObserveAsync(Scope, Glance());
        await alerts.ObserveAsync(Scope, Glance("D01"));
        await alerts.ObserveAsync(Scope, Glance("D01"));
        await alerts.ObserveAsync(Scope, Glance("D01"));

        toasts.Messages.Should().ContainSingle().Which.Message.Should().Contain("D01 is broken");
        toasts.Messages.Single().Level.Should().Be(ToastLevel.Error);
    }

    [Fact]
    public async Task One_that_clears_and_comes_back_is_a_new_appearance()
    {
        ToastService toasts = new();
        HealthAlerts alerts = new(toasts);

        await alerts.ObserveAsync(Scope, Glance());
        await alerts.ObserveAsync(Scope, Glance("D01"));
        await alerts.ObserveAsync(Scope, Glance());
        await alerts.ObserveAsync(Scope, Glance("D01"));

        toasts.Messages.Should().HaveCount(2);
    }

    /// <summary>A poll that knows nothing - no recent sweep - is not the Critical going away.</summary>
    [Fact]
    public async Task A_poll_that_knows_nothing_changes_nothing()
    {
        ToastService toasts = new();
        HealthAlerts alerts = new(toasts);

        await alerts.ObserveAsync(Scope, Glance("D01"));
        await alerts.ObserveAsync(Scope, glance: null);
        await alerts.ObserveAsync(Scope, Glance("D01"));

        toasts.Messages.Should().BeEmpty();
    }

    [Fact]
    public async Task Moving_to_another_database_starts_watching_it_afresh()
    {
        ToastService toasts = new();
        HealthAlerts alerts = new(toasts);

        await alerts.ObserveAsync(Scope, Glance());
        await alerts.ObserveAsync(Scope with { DatabaseId = "other" }, Glance("D01"));

        toasts.Messages.Should().BeEmpty("the first look at the other database only takes note");
    }

    // -----------------------------------------------------------------------------------------------
    // Mutes
    // -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_mute_is_written_to_the_browser_and_read_back_by_the_next_circuit()
    {
        using BunitContext context = new();
        context.JSInterop.Setup<string?>("martenStudio.health.getMuted").SetResult(Stored("default|db|K02"));
        context.JSInterop.SetupVoid("martenStudio.health.setMuted", _ => true).SetVoidResult();

        HealthMutes mutes = new();
        await mutes.LoadAsync(context.JSInterop.JSRuntime, Token);

        mutes.IsMuted("default|db|K02").Should().BeTrue();
        mutes.IsMuted("default|other|K02").Should().BeFalse("a mute is about one database");
        context.JSInterop.Invocations.Should().NotContain(static x => x.Identifier == "martenStudio.health.setMuted",
            "a list of hashes and nothing else is read as it is, and not written back");

        await mutes.SetAsync(context.JSInterop.JSRuntime, "default|db|K03", muted: true, Token);
        await mutes.SetAsync(context.JSInterop.JSRuntime, "default|db|K02", muted: false, Token);

        mutes.Keys.Should().Equal(HealthMutes.Hash("default|db|K03"));

        context.JSInterop.VerifyInvoke("martenStudio.health.setMuted", calledTimes: 2)[1].Arguments.Should().Equal(Stored("default|db|K03"));
    }

    /// <summary>
    /// SEC2 L1: a mute key can carry what its finding's gate guards - E03's subject is <c>tenant/streamName</c> - and
    /// <c>localStorage</c> is readable by every script on the host's origin, so the browser is handed hashes only.
    /// </summary>
    [Fact]
    public async Task No_raw_key_reaches_the_browser()
    {
        using BunitContext context = new();
        context.JSInterop.Setup<string?>("martenStudio.health.getMuted").SetResult(null);
        context.JSInterop.SetupVoid("martenStudio.health.setMuted", _ => true).SetVoidResult();

        const string Key = "default|tenants.acme_db|E03|acme/orders-4711";

        HealthMutes mutes = new();
        await mutes.LoadAsync(context.JSInterop.JSRuntime, Token);
        await mutes.SetAsync(context.JSInterop.JSRuntime, Key, muted: true, Token);
        await mutes.SetAsync(context.JSInterop.JSRuntime, "default|tenants.acme_db|K02", muted: true, Token);

        IReadOnlyList<object?> written = [.. context.JSInterop.Invocations.SelectMany(static x => x.Arguments)];
        written.Should().NotBeEmpty();

        foreach (string argument in written.OfType<string>())
        {
            argument.Should().NotContain("acme").And.NotContain("orders-4711").And.NotContain("|").And.NotContain("E03");

            string[] stored = System.Text.Json.JsonSerializer.Deserialize<string[]>(argument)!;
            stored.Should().OnlyContain(static x => x.Length == 64 && x.All(static c => char.IsAsciiHexDigit(c) && !char.IsAsciiLetterUpper(c)),
                "each entry is a SHA-256 in lowercase hex");
        }

        mutes.Keys.Should().Equal(HealthMutes.Hash(Key), HealthMutes.Hash("default|tenants.acme_db|K02"));
        mutes.IsMuted(Key).Should().BeTrue();
    }

    [Fact]
    public async Task A_mute_still_matches_after_a_reload()
    {
        const string Key = "default|db|E03|acme/orders-4711";

        string written;

        using (BunitContext first = new())
        {
            first.JSInterop.Setup<string?>("martenStudio.health.getMuted").SetResult(null);
            first.JSInterop.SetupVoid("martenStudio.health.setMuted", _ => true).SetVoidResult();

            HealthMutes before = new();
            await before.LoadAsync(first.JSInterop.JSRuntime, Token);
            await before.SetAsync(first.JSInterop.JSRuntime, Key, muted: true, Token);

            written = (string) first.JSInterop.VerifyInvoke("martenStudio.health.setMuted").Arguments.Single()!;
        }

        // The next circuit - a reload, another tab - reads back exactly what the first one wrote.
        using BunitContext second = new();
        second.JSInterop.Setup<string?>("martenStudio.health.getMuted").SetResult(written);
        second.JSInterop.SetupVoid("martenStudio.health.setMuted", _ => true).SetVoidResult();

        HealthMutes after = new();
        await after.LoadAsync(second.JSInterop.JSRuntime, Token);

        after.IsMuted(Key).Should().BeTrue();
        after.IsMuted("default|db|E03|globex/orders-4711").Should().BeFalse();
        second.JSInterop.Invocations.Should().NotContain(static x => x.Identifier == "martenStudio.health.setMuted");
    }

    /// <summary>
    /// A pre-release 0.4 build stored raw keys. They are dropped, not migrated, and the list is written back at once
    /// without them, so a raw key does not wait in the browser for the next mute to overwrite it.
    /// </summary>
    [Fact]
    public async Task A_raw_key_a_pre_release_build_stored_is_dropped_and_the_list_written_back_without_it()
    {
        using BunitContext context = new();
        context.JSInterop.Setup<string?>("martenStudio.health.getMuted")
            .SetResult(System.Text.Json.JsonSerializer.Serialize(new[] { "default|db|E03|acme/orders-4711", HealthMutes.Hash("default|db|K03") }));
        context.JSInterop.SetupVoid("martenStudio.health.setMuted", _ => true).SetVoidResult();

        HealthMutes mutes = new();
        await mutes.LoadAsync(context.JSInterop.JSRuntime, Token);

        mutes.IsMuted("default|db|E03|acme/orders-4711").Should().BeFalse("a raw key is not a hash, and is not carried over");
        mutes.IsMuted("default|db|K03").Should().BeTrue();

        context.JSInterop.VerifyInvoke("martenStudio.health.setMuted").Arguments.Should().Equal(Stored("default|db|K03"));
    }

    /// <summary>What the browser holds for these keys, as <see cref="HealthMutes" /> writes it.</summary>
    private static string Stored(params string[] keys) =>
        System.Text.Json.JsonSerializer.Serialize(keys.Select(HealthMutes.Hash));

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
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"a\":1}")]
    [InlineData("[\"\",null]")]
    [InlineData("[\"default|db|K02\"]")]
    [InlineData("[\"8B2C9E0F8B2C9E0F8B2C9E0F8B2C9E0F8B2C9E0F8B2C9E0F8B2C9E0F8B2C9E0F\"]")]
    public void Anything_stored_that_is_not_a_list_of_hashes_is_nothing(string? stored)
    {
        HealthMutes.Parse(stored).Should().BeEmpty();
    }

    [Fact]
    public void A_stored_list_keeps_its_newest_two_hundred()
    {
        string json = System.Text.Json.JsonSerializer.Serialize(Enumerable.Range(0, 250).Select(static i => HealthMutes.Hash("k" + i)));

        IReadOnlyList<string> keys = HealthMutes.Parse(json, out bool dropped);

        keys.Should().HaveCount(HealthMutes.MaxKeys);
        keys[0].Should().Be(HealthMutes.Hash("k50"));
        dropped.Should().BeTrue("the oldest fifty were left out, so the list is written back");
    }

    [Fact]
    public void A_hash_is_a_lowercase_hex_SHA256_of_the_key_in_UTF8()
    {
        HealthMutes.Hash("abc").Should().Be("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
        HealthMutes.Hash("default|db|E03|acme/orders").Should().NotBe(HealthMutes.Hash("default|db|E03|globex/orders"));
    }
}
