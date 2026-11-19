using Bunit;

using DataStudio.Postgres.Components.Pages.Query;

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace DataStudio.Postgres.Tests.Components;

/// <summary>
/// The SQL editor's buttons act on the text the box holds (jzpn): they read it from the browser first. That read is a round trip, and a
/// visitor who leaves the page within it has disposed the editor and its owner, so the button then does nothing (mxxh): running would
/// run on a disposed panel and end the circuit.
/// </summary>
public class QueryEditorLiveTextTests
{
    private const string ReadValue = "martenStudio.query.readValue";

    private static IRenderedComponent<QueryEditor> Render(PostgresComponentContext context, List<string> ran) =>
        context.Render<QueryEditor>(parameters => parameters
            .Add(x => x.Value, "select 22")
            .Add(x => x.OnRun, EventCallback.Factory.Create<string>(new object(), text => ran.Add(text))));

    [Fact]
    public async Task Run_runs_the_text_the_box_holds_and_not_the_copy_the_server_held()
    {
        await using PostgresComponentContext context = new();
        context.JSInterop.Setup<string?>(ReadValue, _ => true).SetResult("select 11");
        List<string> ran = [];
        IRenderedComponent<QueryEditor> editor = Render(context, ran);

        await editor.Find(".ms-query-run").ClickAsync(new MouseEventArgs());

        ran.Should().Equal("select 11");
    }

    [Fact]
    public async Task A_button_whose_editor_went_away_while_the_box_was_read_runs_nothing()
    {
        await using PostgresComponentContext context = new();
        JSRuntimeInvocationHandler<string?> reading = context.JSInterop.Setup<string?>(ReadValue, _ => true);
        List<string> ran = [];
        IRenderedComponent<QueryEditor> editor = Render(context, ran);

        Task click = editor.Find(".ms-query-run").ClickAsync(new MouseEventArgs());
        reading.Invocations.Should().ContainSingle("the click is waiting for the box's text");

        await editor.Instance.DisposeAsync();
        reading.SetResult("select 11");
        await click;

        ran.Should().BeEmpty("the editor and the panel that owns it are gone: nothing runs on them");
    }

    [Fact]
    public async Task Without_the_script_the_copy_the_server_holds_runs()
    {
        await using PostgresComponentContext context = new();
        List<string> ran = [];
        IRenderedComponent<QueryEditor> editor = Render(context, ran);

        await editor.Find(".ms-query-run").ClickAsync(new MouseEventArgs());

        ran.Should().Equal(["select 22"], "an unanswered read leaves what a change event delivered");
    }
}
