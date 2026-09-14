using AngleSharp.Dom;

using Bunit;

using MartenStudio.Components.Pages.Events;
using MartenStudio.Services;
using MartenStudio.Services.Events;
using MartenStudio.Tests.Components;

using StreamState = MartenStudio.Services.Events.StreamState;

namespace MartenStudio.Tests.Events;

/// <summary>
/// The Events screens' mutating controls against <see cref="MartenStudioOptions.WriteAuthorizationPolicy" />,
/// which is asked about the visitor rather than about the process.
/// </summary>
/// <remarks>
/// <para>
/// A capability is a property of the application; a write policy is a property of the person. The
/// Documents pages have rendered the difference since P8b — a studio with <c>EditDocuments</c> on, mounted
/// for a team where two people may write, showed all forty-two a live Edit button — and Events did not:
/// Archive, Discard, Skip and Rewind were live for everybody the store policy let in, and the refusal
/// arrived as an error toast after the button had been pressed.
/// </para>
/// <para>
/// Disabled and not hidden. "This studio can archive streams and your account may not" is a different
/// fact from "this studio cannot archive streams", and somebody who cannot tell them apart goes and asks
/// the wrong person for the wrong thing.
/// </para>
/// <para>
/// None of this is the enforcement and no test here pretends it is: <c>IEventDataService</c> resolves the
/// scope with the same capability and throws (AGENTS.md hard rule 5), which the service tests and the
/// live suite are about. Every test comes in a pair — a refusing policy and an allowing one — because a
/// control disabled for some other reason would pass the first half on its own.
/// </para>
/// </remarks>
public class EventsWritePolicyTests
{
    private const string WritePolicy = "MartenStudioWriter";
    private const string StreamId = "11111111-1111-1111-1111-111111111111";

    private static readonly Guid LetterId = new("22222222-2222-2222-2222-222222222222");

    // ------------------------------------------------------------------------------------------------
    // Stream detail: Archive
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Archive_is_disabled_and_says_why_for_a_visitor_the_write_policy_refuses()
    {
        using StudioComponentContext context = await RefusingAsync();
        context.EventData.StreamStates[StreamId] = Stream();

        IRenderedComponent<StreamDetail> page = RenderStream(context);

        IElement archive = page.Find(".ms-stream-archive");
        archive.HasAttribute("disabled").Should().BeTrue();
        archive.GetAttribute("title").Should().Contain("may not archive streams here");

        page.Find(".ms-write-refusal").TextContent
            .Should().Be(WritePolicyRefusal.For(StudioCapability.ArchiveStreams));
    }

    [Fact]
    public async Task The_refused_archive_button_is_rendered_rather_than_removed()
    {
        using StudioComponentContext context = await RefusingAsync();
        context.EventData.StreamStates[StreamId] = Stream();

        IRenderedComponent<StreamDetail> page = RenderStream(context);

        page.FindAll(".ms-stream-archive").Should().ContainSingle(
            "hiding it would make this studio look like one whose host never enabled archiving");
        page.FindAll(".ms-capability-disabled").Should().BeEmpty(
            "the capability is on; it is the visitor who was refused");
    }

    [Fact]
    public async Task Archive_is_live_for_a_visitor_the_same_policy_allows()
    {
        using StudioComponentContext context = await AllowingAsync();
        context.EventData.StreamStates[StreamId] = Stream();

        IRenderedComponent<StreamDetail> page = RenderStream(context);

        page.Find(".ms-stream-archive").HasAttribute("disabled").Should().BeFalse();
        page.FindAll(".ms-write-refusal").Should().BeEmpty();
    }

    // ------------------------------------------------------------------------------------------------
    // Dead letters: Discard, Skip and Rewind
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task All_three_dead_letter_actions_are_disabled_for_a_refused_visitor()
    {
        using StudioComponentContext context = await RefusingAsync();
        WithRewindableDeadLetter(context);

        IRenderedComponent<DeadLetters> page = await ExpandAsync(context);

        foreach (string selector in Actions)
        {
            IElement button = page.Find(selector);
            button.HasAttribute("disabled").Should().BeTrue($"'{selector}' is a write");
            button.GetAttribute("title").Should().Contain("may not manage dead letters here");
        }

        page.Find(".ms-dead-letter-actions .ms-write-refusal").TextContent
            .Should().Be(WritePolicyRefusal.For(StudioCapability.ManageDeadLetters));
    }

    /// <summary>
    /// The same three, with a policy that allows. Without this the test above would pass over a page
    /// whose buttons were disabled because the store has no <c>is_skipped</c> column or no daemon here.
    /// </summary>
    [Fact]
    public async Task All_three_dead_letter_actions_are_live_for_a_visitor_the_policy_allows()
    {
        using StudioComponentContext context = await AllowingAsync();
        WithRewindableDeadLetter(context);

        IRenderedComponent<DeadLetters> page = await ExpandAsync(context);

        foreach (string selector in Actions)
        {
            page.Find(selector).HasAttribute("disabled").Should().BeFalse($"'{selector}' should be offered");
        }

        page.FindAll(".ms-write-refusal").Should().BeEmpty();
    }

    /// <summary>
    /// A capability that is simply off still names the option and says nothing about the policy: that
    /// refusal is about the application, is something a host can act on, and the guard reaches it before
    /// a scope is ever resolved.
    /// </summary>
    [Fact]
    public async Task A_capability_that_is_off_names_the_option_rather_than_the_account()
    {
        StudioComponentContext context = new();
        await context.ReadyAsync();
        context.Options.WriteAuthorizationPolicy = WritePolicy;
        context.AuthorizationService.DenyEverything();
        context.EventData.StreamStates[StreamId] = Stream();

        using (context)
        {
            IRenderedComponent<StreamDetail> page = RenderStream(context);

            page.Find(".ms-capability-disabled").TextContent
                .Should().Contain("MartenStudioOptions.Capabilities.ArchiveStreams");
            page.FindAll(".ms-write-refusal").Should().BeEmpty();
            page.FindAll(".ms-stream-archive").Should().BeEmpty();
        }
    }

    // ------------------------------------------------------------------------------------------------
    // Fixtures
    // ------------------------------------------------------------------------------------------------

    /// <summary>The three mutating controls on a dead letter's expanded row.</summary>
    private static string[] Actions =>
        [".ms-dead-letter-discard", ".ms-dead-letter-skip", ".ms-dead-letter-rewind"];

    /// <summary>
    /// Every capability on and a write policy that refuses this visitor.
    /// </summary>
    /// <remarks>
    /// Only the write policy is configured: with no store policy, reads pass without anything being
    /// asked, which is the shape that isolates the write axis. The rule matches on
    /// <c>Capability is null</c>, which is exactly how <c>StudioAuthorization</c> spells a read.
    /// </remarks>
    private static Task<StudioComponentContext> RefusingAsync() =>
        ContextAsync(static resource => resource.Capability is null);

    private static Task<StudioComponentContext> AllowingAsync() => ContextAsync(static _ => true);

    private static async Task<StudioComponentContext> ContextAsync(Func<MartenStoreResource, bool> rule)
    {
        StudioComponentContext context = new();
        context.WithAllCapabilities();
        context.Options.WriteAuthorizationPolicy = WritePolicy;
        context.AuthorizationService.Allow(rule);

        // The shape a dead letter's Skip needs, and a daemon here for its Rewind. Set before anything
        // renders, so a disabled button in the refusing half can only be the policy.
        context.EventData.Shape = context.EventData.Shape with { HasIsSkipped = true };
        context.ProjectionData.WithProjection("OrderSummary");

        await context.ReadyAsync();
        return context;
    }

    private static void WithRewindableDeadLetter(StudioComponentContext context) =>
        context.EventData.DeadLetters = new DeadLetterPage([FakeEventDataService.DeadLetter(LetterId)], false);

    private static IRenderedComponent<StreamDetail> RenderStream(StudioComponentContext context)
    {
        context.Navigate("marten/events/streams/s?id=" + StreamId);
        return context.Render<StreamDetail>();
    }

    private static async Task<IRenderedComponent<DeadLetters>> ExpandAsync(StudioComponentContext context)
    {
        IRenderedComponent<DeadLetters> page = context.Render<DeadLetters>();
        await page.Find("tbody tr td.ms-table-cell-actions button").ClickAsync(new());
        return page;
    }

    private static StreamState Stream() =>
        new(
            StreamId,
            Exists: true,
            "Order",
            3,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            IsArchived: false,
            TenantId: null);
}
