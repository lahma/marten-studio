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
        context.JSInterop.Setup<string?>("martenStudio.health.getMuted").SetResult("[\"default|db|K02\"]");
        context.JSInterop.SetupVoid("martenStudio.health.setMuted", _ => true).SetVoidResult();

        HealthMutes mutes = new();
        await mutes.LoadAsync(context.JSInterop.JSRuntime, Token);

        mutes.IsMuted("default|db|K02").Should().BeTrue();
        mutes.IsMuted("default|other|K02").Should().BeFalse("a mute is about one database");

        await mutes.SetAsync(context.JSInterop.JSRuntime, "default|db|K03", muted: true, Token);
        await mutes.SetAsync(context.JSInterop.JSRuntime, "default|db|K02", muted: false, Token);

        mutes.Keys.Should().Equal("default|db|K03");

        context.JSInterop.VerifyInvoke("martenStudio.health.setMuted", calledTimes: 2)[1].Arguments.Should().Equal("[\"default|db|K03\"]");
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
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"a\":1}")]
    [InlineData("[\"\",null]")]
    public void Anything_stored_that_is_not_a_list_of_keys_is_nothing(string? stored)
    {
        HealthMutes.Parse(stored).Should().BeEmpty();
    }

    [Fact]
    public void A_stored_list_keeps_its_newest_two_hundred()
    {
        string json = System.Text.Json.JsonSerializer.Serialize(Enumerable.Range(0, 250).Select(static i => "k" + i));

        IReadOnlyList<string> keys = HealthMutes.Parse(json);

        keys.Should().HaveCount(HealthMutes.MaxKeys);
        keys[0].Should().Be("k50");
    }
}
