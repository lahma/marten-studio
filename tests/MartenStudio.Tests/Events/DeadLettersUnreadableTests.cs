using Bunit;

using MartenStudio.Components.Pages.Events;
using MartenStudio.Services.Events;
using MartenStudio.Tests.Components;

namespace MartenStudio.Tests.Events;

/// <summary>
/// A database the studio could not read is said so, never answered with "no dead letters".
/// </summary>
/// <remarks>
/// <para>
/// The screen gates its two reads on <c>EventStoreShape.EventTablesExist</c>, because a Marten session
/// query over <c>DeadLetterEvent</c> creates the table and Marten's helper functions on a database that
/// never ran the daemon (AGENTS.md hard rule 14). <c>DescribeAsync</c> answers a failed read
/// conservatively — <c>EventTablesExist</c> is <see langword="false" /> — which is the right way round for
/// the gate and exactly the wrong way round for the empty state: without the shape's own error in the
/// ladder the visitor was told, with no error and no Retry, that no projection in this database has ever
/// failed. That is a screen making a confident statement about something it never read, which is the case
/// the resilience rules exist for.
/// </para>
/// <para>Found by the adversarial review of packet wiring-fix.</para>
/// </remarks>
public class DeadLettersUnreadableTests
{
    [Fact]
    public async Task A_database_that_could_not_be_read_is_an_error_with_a_retry_rather_than_an_empty_list()
    {
        var context = new StudioComponentContext();
        context.EventData.Shape = EventStoreShape.Unavailable(
            new EventDataError("the database is not accepting connections", "08006", CanRetry: true));

        await context.ReadyAsync();

        IRenderedComponent<DeadLetters> page = context.Render<DeadLetters>();

        page.Find(".ms-error-alert").TextContent.Should()
            .Contain("08006").And.Contain("not accepting connections");

        page.FindAll(".ms-empty-title").Should().BeEmpty(
            "'this store has no event storage yet' is a claim about a database this one could not read");

        context.Dispose();
    }

    /// <summary>
    /// And the gate itself still holds: a database that really has no event tables gets the empty state,
    /// not an error, and no read is attempted.
    /// </summary>
    [Fact]
    public async Task A_database_that_genuinely_has_no_event_tables_still_gets_the_empty_state()
    {
        var context = new StudioComponentContext();
        context.EventData.Shape = context.EventData.Shape with { EventTablesExist = false };

        await context.ReadyAsync();

        IRenderedComponent<DeadLetters> page = context.Render<DeadLetters>();

        page.FindAll(".ms-error-alert").Should().BeEmpty();
        page.Find(".ms-empty-title").TextContent.Should().Contain("no event storage");

        context.Dispose();
    }
}
