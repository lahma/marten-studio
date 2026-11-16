using DataStudio.Services.Settings;
using JasperFx.Descriptors;

using MartenStudio.Tests.Support;

using Microsoft.Extensions.DependencyInjection;

using PostgresStudio.Services;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Live;

namespace MartenStudio.Tests.Components;

/// <summary>
/// Shell A2b: which reads of the database's rail take note of the health glance, said outright - the rows of the table <c>NavMenu</c> had before
/// the rail was moved out of it. A read takes note (and so may toast a finding that is newly Critical) when it is the poll's or a scope change's
/// after the start, and takes note of nothing when it is the host's first read, a scope change's before the start, or a mutes change's.
/// </summary>
public class DatabaseModuleRailTests
{
    private static CancellationToken Token => Xunit.TestContext.Current.CancellationToken;

    /// <summary>A host of the rail, as a menu is one: it reads when told that something changed, and does nothing once it is gone.</summary>
    private sealed class Host
    {
        public bool Gone { get; set; }

        public Func<Task> Handler { get; init; } = null!;
    }

    private sealed record Rig(StudioComponentContext Context, IStudioModuleRail Rail, StudioScope Scope)
    {
        /// <summary>A host, as <c>NavMenu</c> is one: it reads when told that something changed.</summary>
        public void Attach() => Rail.Changed += async () => await Rail.ReadBadgesAsync(Token);

        /// <summary>A host that can go: a menu that was disposed answers a change it is still told of by doing nothing.</summary>
        public Host AttachHost()
        {
            Host host = new();
            Host attached = new()
            {
                Handler = async () =>
                {
                    if (!host.Gone)
                    {
                        await Rail.ReadBadgesAsync(Token);
                    }
                },
            };

            host = attached;
            Rail.Changed += attached.Handler;
            return attached;
        }

        /// <summary>The host goes: it is disposed and unsubscribes.</summary>
        public void Detach(Host host)
        {
            host.Gone = true;
            Rail.Changed -= host.Handler;
        }

        /// <summary>The glance a read answers: these findings Critical, for the scope the visitor is on.</summary>
        public HealthGlance Glance(params string[] criticals) =>
            new(DateTimeOffset.UtcNow, criticals.Length, 0, [.. criticals.Select(static x => new HealthGlanceItem("default|db-a|" + x, x + " is broken"))]) { Scope = Scope };

        public IReadOnlyList<string> Toasted => [.. Context.Toasts.Messages.Select(static x => x.Message)];
    }

    private static async Task<Rig> StartAsync(StudioComponentContext context)
    {
        context.Catalog.WithStore("default", "Default", DatabaseCardinality.StaticMultiple, "db-a", "db-b");
        await context.State.EnsureInitializedAsync(Token);

        return new Rig(context, context.Services.GetServices<IStudioModuleRail>().Single(), context.State.ActiveScope!);
    }

    [Fact]
    public async Task A_mutes_change_reads_again_and_takes_note_of_nothing()
    {
        await using StudioComponentContext context = new();
        Rig rig = await StartAsync(context);
        rig.Attach();

        context.NavBadges.Health = rig.Glance();
        await rig.Rail.ReadBadgesAsync(Token);
        await rig.Rail.StartAsync(context.JSInterop.JSRuntime, Token);

        // A finding that is new, and a mutes change that re-reads: the re-read does not toast it, so it is still news at the next poll.
        context.NavBadges.Health = rig.Glance("K01");
        await context.Services.GetRequiredService<HealthMutes>().SetAsync(context.JSInterop.JSRuntime, "default|db-a|K09", muted: true, Token);
        rig.Toasted.Should().BeEmpty("a mutes change re-reads the badge and takes note of nothing");

        await rig.Rail.ReadBadgesAsync(Token);

        rig.Toasted.Should().ContainSingle().Which.Should().Contain("K01 is broken", "the poll is what takes note");
    }

    [Fact]
    public async Task A_scope_change_takes_note_only_after_the_start()
    {
        await using StudioComponentContext context = new();
        Rig rig = await StartAsync(context);
        rig.Attach();

        context.NavBadges.Health = rig.Glance();
        await rig.Rail.ReadBadgesAsync(Token);

        // Before the start: two reads that would have toasted K02 had the first been taken note of.
        context.NavBadges.Health = rig.Glance("K01");
        await context.State.NotifyAsync();
        context.NavBadges.Health = rig.Glance("K01", "K02");
        await context.State.NotifyAsync();
        rig.Toasted.Should().BeEmpty("before the start nothing is taken note of");

        // The start takes the first look at what is already Critical, and says nothing of it.
        await rig.Rail.StartAsync(context.JSInterop.JSRuntime, Token);
        rig.Toasted.Should().BeEmpty("those findings are on screen already");

        // After it, a scope change's read takes note.
        context.NavBadges.Health = rig.Glance("K01", "K02", "K03");
        await context.State.NotifyAsync();

        rig.Toasted.Should().ContainSingle().Which.Should().Contain("K03 is broken");
    }

    [Fact]
    public async Task A_hosts_first_read_takes_note_of_nothing_even_when_the_start_came_first()
    {
        await using StudioComponentContext context = new();
        Rig rig = await StartAsync(context);

        // A first host has looked at this scope's health already, so the circuit's toast is watching it.
        rig.Attach();
        context.NavBadges.Health = rig.Glance();
        await rig.Rail.ReadBadgesAsync(Token);
        await rig.Rail.StartAsync(context.JSInterop.JSRuntime, Token);

        // The layout is re-created: a second host, whose first read is slow, and whose first render starts the rail while it is in flight.
        rig.Attach();
        TaskCompletionSource<NavIndicators> slow = context.NavBadges.HoldNextRead();
        Task<IReadOnlyDictionary<string, StudioNavBadge>?> first = rig.Rail.ReadBadgesAsync(Token).AsTask();
        await rig.Rail.StartAsync(context.JSInterop.JSRuntime, Token);

        slow.SetResult(NavIndicators.None with { Health = rig.Glance("K01") });
        await first;

        rig.Toasted.Should().BeEmpty("a host's first read is never taken note of, whether or not the start has begun");
    }

    [Fact]
    public async Task A_read_the_scope_outran_answers_nothing_and_leaves_the_last_badges_to_the_host()
    {
        await using StudioComponentContext context = new();
        Rig rig = await StartAsync(context);
        rig.Attach();

        TaskCompletionSource<NavIndicators> slow = context.NavBadges.HoldNextRead();
        Task<IReadOnlyDictionary<string, StudioNavBadge>?> read = rig.Rail.ReadBadgesAsync(Token).AsTask();

        await context.State.SetScopeAsync("default", "db-b", null, Token);
        slow.SetResult(NavIndicators.None with { Health = rig.Glance("K01") });

        (await read).Should().BeNull("the scope moved while the read was in flight, so none of it is drawn");
    }

    [Fact]
    public async Task A_cancelled_read_answers_nothing()
    {
        await using StudioComponentContext context = new();
        Rig rig = await StartAsync(context);
        using CancellationTokenSource cancelled = new();

        TaskCompletionSource<NavIndicators> slow = context.NavBadges.HoldNextRead();
        Task<IReadOnlyDictionary<string, StudioNavBadge>?> read = rig.Rail.ReadBadgesAsync(cancelled.Token).AsTask();
        slow.SetException(new OperationCanceledException(cancelled.Token));

        (await read).Should().BeNull();
    }

    [Fact]
    public async Task The_health_badge_is_drawn_from_the_glance_under_its_key_and_the_other_badges_pass_through()
    {
        await using StudioComponentContext context = new();
        Rig rig = await StartAsync(context);
        context.NavBadges.DeadLetters = 3;
        context.NavBadges.Health = rig.Glance("K01");

        IReadOnlyDictionary<string, StudioNavBadge>? badges = await rig.Rail.ReadBadgesAsync(Token);

        badges.Should().NotBeNull();
        badges![StudioNavBadgeKeys.Health].Text.Should().Be("1");
        badges[StudioNavBadgeKeys.Health].CssClass.Should().Be("ms-nav-badge-danger");
        badges[StudioNavBadgeKeys.Health].Title.Should().StartWith("1 critical finding, as of ");
        badges.Keys.Should().Contain(static x => x != StudioNavBadgeKeys.Health, "the dead-letter badge passes through as the indicator service read it");
    }

    [Fact]
    public async Task An_entry_with_a_condition_key_the_rail_does_not_know_is_not_shown()
    {
        await using StudioComponentContext context = new();
        Rig rig = await StartAsync(context);

        rig.Rail.IsShown(new StudioNavItem("Plain", "plain", "grid")).Should().BeTrue("no key, no condition");
        rig.Rail.IsShown(new StudioNavItem("Odd", "odd", "grid") { ShownWhen = "made-up" }).Should().BeFalse("an unknown key fails closed");
    }

    /// <summary>
    /// Review of A2b, F2: <c>HealthMutes</c> walks a snapshot of its handlers, so the relay of a host that has gone can still be called while a
    /// mutes change is being told. It must set nothing: its note ("this read is a mutes change's, take note of nothing") would be consumed by the
    /// next host's first scope change, which would then not take note of a finding that turned Critical in between.
    /// </summary>
    [Fact]
    public async Task A_host_that_has_gone_cannot_leave_the_mutes_note_for_the_next_host()
    {
        await using StudioComponentContext context = new();
        Rig rig = await StartAsync(context);
        HealthMutes mutes = context.Services.GetRequiredService<HealthMutes>();
        await mutes.LoadAsync(context.JSInterop.JSRuntime, Token);

        // An earlier listener of the mutes change that is still working when the layout is re-created.
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        mutes.Changed += async () =>
        {
            entered.TrySetResult();
            await release.Task.Bounded();
        };

        Host first = rig.AttachHost();
        context.NavBadges.Health = rig.Glance();
        await rig.Rail.ReadBadgesAsync(Token);
        await rig.Rail.StartAsync(context.JSInterop.JSRuntime, Token);

        Task mute = mutes.SetAsync(context.JSInterop.JSRuntime, "default|db-a|K09", muted: true, Token);
        await entered.Task.Bounded();

        // The layout is re-created while the mutes change is being told: the first host is gone, the second starts.
        rig.Detach(first);
        rig.AttachHost();
        await rig.Rail.ReadBadgesAsync(Token);
        await rig.Rail.StartAsync(context.JSInterop.JSRuntime, Token);

        // The snapshot reaches the first host's relay now.
        release.SetResult();
        await mute;

        // A finding turns Critical and the visitor changes scope: the second host's read takes note of it, as no mutes change was its own.
        context.NavBadges.Health = rig.Glance("K01");
        await context.State.NotifyAsync();

        rig.Toasted.Should().ContainSingle().Which.Should().Contain("K01 is broken", "the gone host's relay left no note for this read to consume");
    }

    /// <summary>Review of A2b, F3: a host that attaches starts afresh - a re-created layout's scope change before its own start takes note of nothing.</summary>
    [Fact]
    public async Task A_re_attached_host_has_not_started_until_it_starts()
    {
        await using StudioComponentContext context = new();
        Rig rig = await StartAsync(context);

        Host first = rig.AttachHost();
        context.NavBadges.Health = rig.Glance();
        await rig.Rail.ReadBadgesAsync(Token);
        await rig.Rail.StartAsync(context.JSInterop.JSRuntime, Token);

        // The layout is re-created: the first host goes, a second attaches and has read once, and has not started.
        rig.Detach(first);
        rig.AttachHost();
        await rig.Rail.ReadBadgesAsync(Token);

        context.NavBadges.Health = rig.Glance("K01");
        await context.State.NotifyAsync();

        rig.Toasted.Should().BeEmpty("the new host has not started, and nothing is taken note of before the start");

        // The start looks at the last read, which the circuit's toast has not seen Critical before, so K01 is told now; and after it a scope change takes note again.
        await rig.Rail.StartAsync(context.JSInterop.JSRuntime, Token);
        rig.Toasted.Should().ContainSingle().Which.Should().Contain("K01 is broken");

        context.NavBadges.Health = rig.Glance("K01", "K02");
        await context.State.NotifyAsync();

        rig.Toasted.Should().HaveCount(2).And.Contain(static x => x.Contains("K02 is broken", StringComparison.Ordinal));
    }
}
