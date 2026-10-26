using Bunit;

using MartenStudio.Services;

using Microsoft.Extensions.DependencyInjection;
using PostgresStudio.Services;
using PostgresStudio.Services.Health;

namespace MartenStudio.Tests.Health;

/// <summary>
/// The toast for a Critical that appears while somebody watches - once, and not again for a finding that keeps coming
/// back inside the cool-down. The mutes that live in the browser and nowhere else are <see cref="HealthMutesTests" />'.
/// </summary>
public class HealthAlertsTests
{
    private static readonly StudioScope Scope = new("default", "localhost.marten", null);

    private static readonly DateTimeOffset Now = new(2026, 10, 15, 9, 0, 0, TimeSpan.Zero);

    /// <summary>The sample's and the default's health interval: one sweep a minute.</summary>
    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);

    private static CancellationToken Token => Xunit.TestContext.Current.CancellationToken;

    /// <summary>What the badge read for <see cref="Scope" /> from the sweep measured <paramref name="minutes" /> after <see cref="Now" />.</summary>
    private static HealthGlance Glance(double minutes, params string[] criticals) => GlanceFor(Scope, minutes, criticals);

    /// <summary>
    /// What the badge read for <paramref name="scope" />, stamped with it as the health service stamps a glance, with mute
    /// keys of that database's.
    /// </summary>
    private static HealthGlance GlanceFor(StudioScope scope, double minutes, params string[] criticals) =>
        new(Now.AddMinutes(minutes), criticals.Length, 0, [.. criticals.Select(x => new HealthGlanceItem(scope.StoreKey + "|" + scope.DatabaseId + "|" + x, x + " is broken"))])
        {
            Scope = scope,
        };

    private static HealthAlerts Alerts(ToastService toasts, Action<MartenStudioOptions>? configure = null)
    {
        MartenStudioOptions options = new();
        configure?.Invoke(options);
        return new HealthAlerts(toasts, MartenStudioSettings.Of(options));
    }

    [Fact]
    public async Task What_is_already_Critical_at_the_first_look_is_not_toasted()
    {
        ToastService toasts = new();
        HealthAlerts alerts = Alerts(toasts);

        await alerts.ObserveAsync(Scope, Glance(0, "D01"));

        toasts.Messages.Should().BeEmpty("it is on the page and in the badge already; a toast would say it just appeared");
    }

    [Fact]
    public async Task A_Critical_that_appears_while_watching_is_toasted_once()
    {
        ToastService toasts = new();
        HealthAlerts alerts = Alerts(toasts);

        await alerts.ObserveAsync(Scope, Glance(0));
        await alerts.ObserveAsync(Scope, Glance(1, "D01"));
        await alerts.ObserveAsync(Scope, Glance(2, "D01"));
        await alerts.ObserveAsync(Scope, Glance(60, "D01"));

        toasts.Messages.Should().ContainSingle().Which.Message.Should().Contain("D01 is broken");
        toasts.Messages.Single().Level.Should().Be(ToastLevel.Error);
    }

    /// <summary>
    /// What the 0.4 UI sweep watched: C03 at about 95% of connections, Critical one sweep and a Warning the next, for as
    /// long as the usage hovers there. Inside the cool-down that is one toast, not one every other minute.
    /// </summary>
    [Fact]
    public async Task A_Critical_that_flaps_inside_the_cool_down_is_toasted_once()
    {
        ToastService toasts = new();
        HealthAlerts alerts = Alerts(toasts);

        await alerts.ObserveAsync(Scope, Glance(0));

        for (int sweep = 1; sweep <= 30; sweep++)
        {
            await alerts.ObserveAsync(Scope, sweep % 2 == 1 ? Glance(sweep, "C03") : Glance(sweep));
        }

        toasts.Messages.Should().ContainSingle("fifteen returns in half an hour are each inside the cool-down of the one before")
            .Which.Message.Should().Contain("C03 is broken");
    }

    /// <summary>
    /// Gone for the whole cool-down - fifteen minutes from the last sweep that found it, at the default interval - its
    /// return is news again; a second short of it, it is the same flap.
    /// </summary>
    [Theory]
    [InlineData(14 * 60 + 59, 1)]
    [InlineData(15 * 60, 2)]
    [InlineData(40 * 60, 2)]
    public async Task One_that_comes_back_after_the_cool_down_is_toasted_again(int secondsAway, int toastsExpected)
    {
        ToastService toasts = new();
        HealthAlerts alerts = Alerts(toasts);

        await alerts.ObserveAsync(Scope, Glance(0));
        await alerts.ObserveAsync(Scope, Glance(1, "C03"));
        await alerts.ObserveAsync(Scope, Glance(2));
        await alerts.ObserveAsync(Scope, Glance(1 + (secondsAway / 60.0), "C03"));

        toasts.Messages.Should().HaveCount(toastsExpected);
        toasts.Messages.Should().OnlyContain(static x => x.Message.Contains("C03 is broken"));
    }

    [Fact]
    public async Task Another_finding_during_the_cool_down_is_toasted_at_once()
    {
        ToastService toasts = new();
        HealthAlerts alerts = Alerts(toasts);

        await alerts.ObserveAsync(Scope, Glance(0));
        await alerts.ObserveAsync(Scope, Glance(1, "C03"));
        await alerts.ObserveAsync(Scope, Glance(2));
        await alerts.ObserveAsync(Scope, Glance(3, "D01"));
        await alerts.ObserveAsync(Scope, Glance(4, "C03", "D01"));
        await alerts.ObserveAsync(Scope, Glance(5, "C03", "D01", "E03|acme/orders-4711"));

        toasts.Messages.Select(static x => x.Message).Should().SatisfyRespectively(
            static first => first.Should().Contain("C03 is broken"),
            static second => second.Should().Contain("D01 is broken", "another code is in nobody else's cool-down"),
            static third => third.Should().Contain("E03|acme/orders-4711 is broken", "and so is another subject"));
    }

    /// <summary>
    /// The first look is unchanged - what is already Critical is not toasted - and what it noted is seen: one that was
    /// Critical when the tab arrived and then flaps is the same flap, until it has stayed away for the cool-down.
    /// </summary>
    [Fact]
    public async Task What_the_first_look_noted_is_not_toasted_when_it_flaps_inside_the_cool_down()
    {
        ToastService toasts = new();
        HealthAlerts alerts = Alerts(toasts);

        await alerts.ObserveAsync(Scope, Glance(0, "C03"));
        await alerts.ObserveAsync(Scope, Glance(1));
        await alerts.ObserveAsync(Scope, Glance(2, "C03"));
        await alerts.ObserveAsync(Scope, Glance(3));

        toasts.Messages.Should().BeEmpty();

        await alerts.ObserveAsync(Scope, Glance(18, "C03"));

        toasts.Messages.Should().ContainSingle("gone from minute 2 to minute 18 is longer than the cool-down");
    }

    /// <summary>
    /// The cool-down is a quarter of an hour, or three of the advisor's real intervals when that is longer - never as
    /// short as the two a one-sweep absence measures, from the last sweep that found it.
    /// </summary>
    [Theory]
    [InlineData(60, 5, 15 * 60)]
    [InlineData(15, 5, 15 * 60)]
    [InlineData(10 * 60, 5, 30 * 60)]
    [InlineData(60 * 60, 5, 3 * 60 * 60)]
    [InlineData(60, 5 * 60, 15 * 60)]
    public void The_cool_down_is_a_quarter_of_an_hour_or_three_intervals_whichever_is_longer(int healthSeconds, int refreshSeconds, int expectedSeconds)
    {
        HealthAlerts alerts = Alerts(new ToastService(), x =>
        {
            x.HealthInterval = TimeSpan.FromSeconds(healthSeconds);
            x.RefreshInterval = TimeSpan.FromSeconds(refreshSeconds);
        });

        alerts.CoolDown.Should().Be(TimeSpan.FromSeconds(expectedSeconds));
        alerts.CoolDown.Should().BeGreaterThan(2 * TimeSpan.FromSeconds(Math.Max(healthSeconds, refreshSeconds)),
            "never at or below the two intervals a one-sweep absence measures");
    }

    /// <summary>With an hour between sweeps, a finding that is Critical every other sweep still toasts once.</summary>
    [Fact]
    public async Task With_a_long_interval_a_one_sweep_flap_is_still_one_toast()
    {
        ToastService toasts = new();
        HealthAlerts alerts = Alerts(toasts, static x => x.HealthInterval = TimeSpan.FromHours(1));

        await alerts.ObserveAsync(Scope, Glance(0));

        for (int sweep = 1; sweep <= 12; sweep++)
        {
            await alerts.ObserveAsync(Scope, sweep % 2 == 1 ? Glance(sweep * 60, "C03") : Glance(sweep * 60));
        }

        toasts.Messages.Should().ContainSingle();
    }

    /// <summary>A poll that knows nothing - no recent sweep - is not the Critical going away.</summary>
    [Fact]
    public async Task A_poll_that_knows_nothing_changes_nothing()
    {
        ToastService toasts = new();
        HealthAlerts alerts = Alerts(toasts);

        await alerts.ObserveAsync(Scope, Glance(0, "D01"));
        await alerts.ObserveAsync(Scope, glance: null);
        await alerts.ObserveAsync(Scope, Glance(1, "D01"));

        toasts.Messages.Should().BeEmpty();
    }

    [Fact]
    public async Task Moving_to_another_database_starts_watching_it_afresh()
    {
        ToastService toasts = new();
        HealthAlerts alerts = Alerts(toasts);

        StudioScope other = Scope with { DatabaseId = "other" };

        await alerts.ObserveAsync(Scope, Glance(0));
        await alerts.ObserveAsync(other, GlanceFor(other, 1, "D01"));

        toasts.Messages.Should().BeEmpty("the first look at the other database only takes note");
    }

    /// <summary>
    /// The sidebar's poll and a scope change race: a read for database A that was in flight when the visitor moved to B
    /// lands after the move. Observed with B it is nothing - no toast, though it carries a Critical new to A - and it
    /// spends no first look, so B's first real glance is B's first look and B's existing Critical is not toasted as new.
    /// </summary>
    [Fact]
    public async Task A_glance_read_for_the_previous_scope_and_handed_in_after_the_switch_toasts_nothing()
    {
        ToastService toasts = new();
        HealthAlerts alerts = Alerts(toasts);
        StudioScope a = Scope;
        StudioScope b = Scope with { DatabaseId = "other" };

        await alerts.ObserveAsync(a, GlanceFor(a, 0, "C03"));
        await alerts.ObserveAsync(a, GlanceFor(a, 1, "C03"));

        // The move to B; the poll's read for A arrives after it.
        await alerts.ObserveAsync(b, GlanceFor(a, 2, "C03", "D01"));

        toasts.Messages.Should().BeEmpty("a glance read for A says nothing about B, and the visitor is no longer on A");

        // B's first real glance only takes note: K01 was Critical when the visitor arrived.
        await alerts.ObserveAsync(b, GlanceFor(b, 2, "K01"));
        await alerts.ObserveAsync(b, GlanceFor(b, 3, "K01"));

        toasts.Messages.Should().BeEmpty("K01 was already Critical at B's first look, and B's first look was B's own glance");

        // And B is watched from there on.
        await alerts.ObserveAsync(b, GlanceFor(b, 4, "K01", "T11"));

        toasts.Messages.Should().ContainSingle().Which.Message.Should().Contain("T11 is broken");
    }

    /// <summary>A glance nobody stamped with the scope it was read for cannot be placed, so it is observed as nothing.</summary>
    [Fact]
    public async Task A_glance_that_does_not_say_which_scope_it_was_read_for_changes_nothing()
    {
        ToastService toasts = new();
        HealthAlerts alerts = Alerts(toasts);

        await alerts.ObserveAsync(Scope, Glance(0));
        await alerts.ObserveAsync(Scope, Glance(1, "C03") with { Scope = null });

        toasts.Messages.Should().BeEmpty();

        await alerts.ObserveAsync(Scope, Glance(2, "C03"));

        toasts.Messages.Should().ContainSingle("the unstamped glance noted nothing, so C03 is new at minute 2");
    }

    /// <summary>
    /// The real service stamps the glance with the scope it was asked for - what the toast compares with the sidebar's
    /// scope - as asked and not as resolved: a store key spelt in another case reaches the same sweep, whose mute keys
    /// name the registered key, but the stamp is the visitor's spelling, because that is what the sidebar holds.
    /// </summary>
    [Fact]
    public async Task The_health_service_stamps_a_glance_with_the_scope_it_was_read_for()
    {
        await using HealthHarness harness = HealthHarness.Create();
        harness.Script.Finds("K91", HealthSeverity.Critical);

        await harness.Health.GetReportAsync(harness.Scope, HealthReportRequest.Regular, Token);
        HealthGlance glance = (await harness.Health.GlanceAsync(harness.Scope, Token))!;

        glance.Scope.Should().Be(harness.Scope);
        glance.Criticals.Should().ContainSingle();

        StudioScope shouted = harness.Scope with { StoreKey = "DEFAULT" };
        HealthGlance spelt = (await harness.Health.GlanceAsync(shouted, Token))!;

        spelt.Criticals.Should().ContainSingle().Which.MuteKey.Should().StartWith("default|", "the finding is the registered store's");
        spelt.Scope.Should().Be(shouted, "the stamp is the scope as asked, not as resolved");
    }

    /// <summary>The cool-down is the circuit's: a look at another database and back does not start it again.</summary>
    [Fact]
    public async Task Leaving_for_another_database_and_coming_back_keeps_the_cool_down()
    {
        ToastService toasts = new();
        HealthAlerts alerts = Alerts(toasts);
        StudioScope other = Scope with { DatabaseId = "other" };

        await alerts.ObserveAsync(Scope, Glance(0));
        await alerts.ObserveAsync(Scope, Glance(1, "C03"));
        await alerts.ObserveAsync(other, GlanceFor(other, 2));
        await alerts.ObserveAsync(Scope, Glance(3));
        await alerts.ObserveAsync(Scope, Glance(4, "C03"));

        toasts.Messages.Should().ContainSingle("C03 came back three minutes after this circuit last saw it");
    }

    /// <summary>
    /// Through the real service, sweeper and mutes: a Critical muted in this browser is not in the glance, so it never
    /// toasts - not when it appears, and not when it comes back after any cool-down - while one that is not muted
    /// still does, which is what keeps the first half from passing for a pipeline that toasts nothing at all.
    /// </summary>
    [Fact]
    public async Task A_muted_Critical_never_toasts_however_long_it_stays_away()
    {
        await using HealthHarness harness = HealthHarness.Create();
        ToastService toasts = harness.Circuit.GetRequiredService<ToastService>();
        HealthAlerts alerts = harness.Circuit.GetRequiredService<HealthAlerts>();

        // The first look: nothing is Critical yet.
        await ObserveNextSweepAsync();

        // K91 turns Critical on the next sweep, and is muted from the Health page before the badge's next poll.
        harness.Script.Finds("K91", HealthSeverity.Critical, "Muted critical");
        harness.Clock.Advance(Minute);
        HealthReport report = await harness.Health.GetReportAsync(harness.Scope, HealthReportRequest.Regular, Token);

        using BunitContext js = new();
        js.JSInterop.Mode = JSRuntimeMode.Loose;
        await harness.Mutes.SetAsync(js.JSInterop.JSRuntime, report.Findings.Single(static x => x.Finding.Code == "K91").MuteKey, muted: true, Token);
        await alerts.ObserveAsync(harness.Scope, await harness.Health.GlanceAsync(harness.Scope, Token));

        // It flaps, and stays away for longer than the cool-down, and comes back.
        harness.Script.Judge.Remove("K91");
        await ObserveNextSweepAsync();
        harness.Script.Finds("K91", HealthSeverity.Critical, "Muted critical");
        await ObserveNextSweepAsync();
        harness.Script.Judge.Remove("K91");
        await ObserveNextSweepAsync(after: alerts.CoolDown);
        harness.Script.Finds("K91", HealthSeverity.Critical, "Muted critical");
        await ObserveNextSweepAsync(after: alerts.CoolDown);

        toasts.Messages.Should().BeEmpty("a muted finding is not in the glance");

        harness.Script.Finds("K90", HealthSeverity.Critical, "Unmuted critical");
        await ObserveNextSweepAsync();

        toasts.Messages.Should().ContainSingle().Which.Message.Should().Contain("Unmuted critical");

        async Task ObserveNextSweepAsync(TimeSpan? after = null)
        {
            harness.Clock.Advance(after ?? Minute);
            await harness.Health.GetReportAsync(harness.Scope, HealthReportRequest.Regular, Token);
            await alerts.ObserveAsync(harness.Scope, await harness.Health.GlanceAsync(harness.Scope, Token));
        }
    }

}
