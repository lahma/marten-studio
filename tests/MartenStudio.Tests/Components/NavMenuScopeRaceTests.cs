using AngleSharp.Dom;

using Bunit;

using JasperFx.Descriptors;

using MartenStudio.Components.Layout;
using MartenStudio.Services;
using MartenStudio.Services.Health;
using MartenStudio.Services.Live;

namespace MartenStudio.Tests.Components;

/// <summary>
/// The sidebar's half of the scope race: its poll runs on the thread pool and a scope change on the renderer, and both
/// write the badges, so a read that was in flight when the visitor moved to another database can land after the move.
/// </summary>
public class NavMenuScopeRaceTests
{
    private static CancellationToken Token => Xunit.TestContext.Current.CancellationToken;

    /// <summary>
    /// A read for database A is held in flight; the visitor moves to B, whose own read answers at once; then A's read
    /// lands, carrying A's dead-letter count and a Critical new to A. The sidebar draws only B's count - A's read is not
    /// drawn - and nothing is toasted: A's glance is not B's first look, and B's existing Critical was B's.
    /// </summary>
    [Fact]
    public async Task A_read_the_scope_outran_is_not_drawn_and_its_glance_is_not_observed()
    {
        await using StudioComponentContext context = new();

        // No poll inside the test: the live loop waits a whole interval before its first tick.
        context.Options.RefreshInterval = TimeSpan.FromMinutes(5);
        context.Catalog.WithStore("default", "Default", DatabaseCardinality.StaticMultiple, "db-a", "db-b");
        await context.State.EnsureInitializedAsync(Token);

        StudioScope a = context.State.ActiveScope!;
        StudioScope b = a with { DatabaseId = "db-b" };
        a.DatabaseId.Should().Be("db-a");

        // The read NavMenu starts as it initialises - for A - is held in flight. Every other read answers at once, and
        // while the visitor is on A, with A's count.
        TaskCompletionSource<NavIndicators> readForA = context.NavBadges.HoldNextRead();
        context.NavBadges.DeadLetters = 42;

        IRenderedComponent<NavMenu> nav = context.Render<NavMenu>();

        // A's count is drawn, from the read the browser's mutes loading triggers once the sidebar is interactive.
        nav.WaitForAssertion(() => DeadLetterBadge(nav).Should().Be("42"));

        // The move to B, whose read answers at once with B's count and a Critical B already has.
        context.NavBadges.DeadLetters = 7;
        context.NavBadges.Health = new HealthGlance(DateTimeOffset.UtcNow, 1, 0, [new HealthGlanceItem("default|db-b|K01", "B is broken")])
        {
            Scope = b,
        };

        await nav.InvokeAsync(() => context.State.SetScopeAsync("default", "db-b", tenantId: null, Token));

        context.State.ActiveScope.Should().Be(b);
        nav.WaitForAssertion(() => DeadLetterBadge(nav).Should().Be("7"));

        // A's read lands after the move. NavMenu then finishes initialising and renders once more, so what it draws after
        // that render is what A's read left behind.
        int renders = nav.RenderCount;

        readForA.SetResult(new NavIndicators(MartenStudio.Services.MartenNavBadges.From(42, false, null))
        {
            Health = new HealthGlance(DateTimeOffset.UtcNow, 1, 0, [new HealthGlanceItem("default|db-a|D01", "A is broken")])
            {
                Scope = a,
            },
        });

        nav.WaitForState(() => nav.RenderCount > renders, TimeSpan.FromSeconds(5));

        DeadLetterBadge(nav).Should().Be("7", "A's read was for a database the visitor has left, so B's count stays drawn");
        context.Toasts.Messages.Should().BeEmpty(
            "A's glance is not observed under B, and K01 was already Critical at B's first look");
    }

    /// <summary>The dead-letter entry's count, or <see langword="null" /> when it wears none.</summary>
    private static string? DeadLetterBadge(IRenderedComponent<NavMenu> nav)
    {
        IElement link = nav.FindAll("a.ms-nav-link").Single(static x => x.GetAttribute("href") == "events/dead-letters");
        return link.QuerySelector(".ms-nav-badge")?.TextContent.Trim();
    }
}
