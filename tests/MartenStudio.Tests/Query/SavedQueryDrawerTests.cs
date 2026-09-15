using Bunit;

using MartenStudio.Components.Pages.Query;
using MartenStudio.Tests.Components;

namespace MartenStudio.Tests.Query;

/// <summary>
/// The recent-query list, and the thing that could end a session in it.
/// </summary>
public class SavedQueryDrawerTests
{
    /// <summary>
    /// Two runs of the same text against different collections render as two rows, not as a dead circuit.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The drawer used to key these rows on the query text. <c>SavedQueryStore.AddRecent</c> dedupes on
    /// text <em>and</em> mode <em>and</em> alias, and the drawer renders the whole list for the mode
    /// without filtering by alias — so running one where clause against collection A and then against
    /// collection B put two siblings under one key. A duplicate <c>@key</c> throws inside Blazor's diff
    /// builder, which is not somewhere a component can catch anything: the page does not show an error,
    /// the circuit ends.
    /// </para>
    /// <para>
    /// It is the same defect as issue #1 and it needs no capability at all to reach — the Marten where
    /// clause mode is open to any visitor who can see the studio. Found while reviewing the fix for that
    /// issue, which is why it is fixed alongside it.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task The_same_query_run_against_two_collections_lists_twice_rather_than_ending_the_circuit()
    {
        using var context = new StudioComponentContext();

        IRenderedComponent<SavedQueryDrawer> drawer = context.Render<SavedQueryDrawer>(parameters =>
            parameters.Add(x => x.StoreKey, "default").Add(x => x.Mode, "marten"));

        const string Text = "x.Name == \"bob\"";

        await drawer.Instance.RecordRunAsync(Text, "customer");
        await drawer.Instance.RecordRunAsync(Text, "order");

        // Not decoration: with the fix reverted the first render of a list with two identical keys throws
        // nothing and the render after it does, so a test without this passes either way.
        drawer.Render();

        drawer.Instance.Count.Should().Be(2, "the two runs differ by alias, so neither replaces the other");
        drawer.FindAll(".ms-query-drawer-recent .ms-query-drawer-item").Should().HaveCount(2);
    }
}
