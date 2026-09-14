using AngleSharp.Dom;
using Bunit;
using MartenStudio.Components.Json;
using Microsoft.AspNetCore.Components;

namespace MartenStudio.Tests.Json;

public class JsonDiffTests
{
    [Fact]
    public void Each_bucket_renders_with_its_own_colour_class()
    {
        using var context = new JsonTestContext();

        var view = context.Render<JsonDiff>(p => p
            .Add(c => c.Before, """{"gone":1,"same":2,"moved":3}""")
            .Add(c => c.After, """{"same":2,"moved":4,"new":5}"""));

        view.FindAll(".ms-diff-row.ms-diff-del").Should().ContainSingle();
        view.FindAll(".ms-diff-row.ms-diff-chg").Should().ContainSingle();
        view.FindAll(".ms-diff-row.ms-diff-add").Should().ContainSingle();

        view.Find(".ms-diff-row.ms-diff-del .ms-diff-path").TextContent.Should().Be("$.gone");
        view.Find(".ms-diff-row.ms-diff-chg .ms-diff-before").TextContent.Should().Be("3");
        view.Find(".ms-diff-row.ms-diff-chg .ms-diff-after").TextContent.Should().Be("4");
        view.Find(".ms-diff-row.ms-diff-add .ms-diff-path").TextContent.Should().Be("$.new");
    }

    [Fact]
    public void A_bucket_with_nothing_in_it_is_not_rendered_at_all()
    {
        using var context = new JsonTestContext();

        var view = context.Render<JsonDiff>(p => p
            .Add(c => c.Before, """{"a":1}""")
            .Add(c => c.After, """{"a":1,"b":2}"""));

        view.FindAll(".ms-diff-bucket").Should().ContainSingle();
        view.Find(".ms-diff-title").TextContent.Should().Contain("Added");
    }

    [Fact]
    public void Two_documents_that_say_the_same_thing_render_the_empty_line()
    {
        using var context = new JsonTestContext();

        var view = context.Render<JsonDiff>(p => p
            .Add(c => c.Before, """{"a":1.0,"b":2}""")
            .Add(c => c.After, """{"b":2,"a":1}"""));

        view.FindAll(".ms-diff-bucket").Should().BeEmpty();
        view.Find(".ms-diff-empty").TextContent.Should().Be("The two documents are the same.");
    }

    [Fact]
    public void The_round_trip_body_leads_with_what_saving_will_cost()
    {
        using var context = new JsonTestContext();

        var view = context.Render<RoundTripDiff>(p => p
            .Add(c => c.Edited, """{"name":"Ada","legacy":1,"extra":2}""")
            .Add(c => c.RoundTripped, """{"name":"Ada"}""")
            .Add(c => c.DocumentTypeName, "Person"));

        view.Find(".ms-round-trip-summary").TextContent.Should().Contain("2 properties will be dropped.");
        view.Find(".ms-round-trip-summary").TextContent.Should().Contain("Saving goes through Person.");
        view.Find(".ms-round-trip-summary").ClassList.Should().Contain("ms-alert-warning");
        view.Instance.Loss.Should().BeTrue();
        view.FindAll(".ms-diff-row.ms-diff-del").Should().HaveCount(2);
    }

    [Fact]
    public void A_document_that_survives_the_round_trip_says_nothing_will_change()
    {
        using var context = new JsonTestContext();

        var view = context.Render<RoundTripDiff>(p => p
            .Add(c => c.Edited, """{"name":"Ada"}""")
            .Add(c => c.RoundTripped, """{"name":"Ada"}"""));

        view.Find(".ms-round-trip-summary").TextContent.Should().Contain("Nothing will change.");
        view.Instance.Loss.Should().BeFalse();
        view.Find(".ms-diff-empty").TextContent.Should().Contain("the document survives the round trip unchanged");
    }

    [Fact]
    public void The_stored_document_can_be_shown_alongside_so_the_user_sees_their_own_edit_too()
    {
        using var context = new JsonTestContext();

        var view = context.Render<RoundTripDiff>(p => p
            .Add(c => c.Stored, """{"name":"Ada"}""")
            .Add(c => c.Edited, """{"name":"Grace"}""")
            .Add(c => c.RoundTripped, """{"name":"Grace"}"""));

        view.Find(".ms-round-trip-changes summary").TextContent.Should().Be("Your changes to the stored document");
        view.FindAll(".ms-diff-row.ms-diff-chg").Should().ContainSingle();
    }

    [Fact]
    public void The_actions_appear_only_when_somebody_is_listening_for_them()
    {
        using var context = new JsonTestContext();
        var confirmed = 0;

        var withoutActions = context.Render<RoundTripDiff>(p => p
            .Add(c => c.Edited, """{"a":1}""")
            .Add(c => c.RoundTripped, """{"a":1}"""));
        withoutActions.FindAll(".ms-round-trip-actions").Should().BeEmpty();

        var withActions = context.Render<RoundTripDiff>(p => p
            .Add(c => c.Edited, """{"a":1,"b":2}""")
            .Add(c => c.RoundTripped, """{"a":1}""")
            .Add(c => c.OnConfirm, EventCallback.Factory.Create(this, () => confirmed++)));

        var confirm = withActions.Find(".ms-round-trip-actions button");
        confirm.TextContent.Trim().Should().Be("Save anyway");
        confirm.ClassList.Should().Contain("ms-btn-danger", "confirming a loss is not a routine save");

        confirm.Click();
        confirmed.Should().Be(1);
    }

    [Fact]
    public void Invalid_json_on_either_side_renders_the_empty_state_rather_than_throwing()
    {
        using var context = new JsonTestContext();

        var view = context.Render<JsonDiff>(p => p
            .Add(c => c.Before, "{ nope")
            .Add(c => c.After, """{"a":1}"""));

        view.Find(".ms-diff-empty").Should().NotBeNull();
    }

    [Fact]
    public void A_new_pair_of_documents_re_runs_the_diff()
    {
        using var context = new JsonTestContext();

        var view = context.Render<JsonDiff>(p => p
            .Add(c => c.Before, """{"a":1}""")
            .Add(c => c.After, """{"a":1}"""));
        view.FindAll(".ms-diff-bucket").Should().BeEmpty();

        view.Render(p => p.Add(c => c.After, """{"a":2}"""));

        view.FindAll(".ms-diff-row.ms-diff-chg").Should().ContainSingle();
    }

    [Fact]
    public void Both_sides_of_a_change_are_readable_even_when_the_colour_is_not()
    {
        using var context = new JsonTestContext();

        var view = context.Render<JsonDiff>(p => p
            .Add(c => c.Before, """{"a":1}""")
            .Add(c => c.After, """{"a":2}"""));

        IElement marker = view.Find(".ms-diff-title .ms-diff-marker");
        marker.TextContent.Should().NotBeNullOrWhiteSpace("the bucket carries a glyph as well as a colour");
        marker.GetAttribute("aria-hidden").Should().Be("true");
    }
}
