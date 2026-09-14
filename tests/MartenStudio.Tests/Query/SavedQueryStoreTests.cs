using MartenStudio.Services.Query;

namespace MartenStudio.Tests.Query;

/// <summary>
/// The recent and saved query lists, which live in the browser and therefore have to survive whatever
/// comes back out of it.
/// </summary>
public class SavedQueryStoreTests
{
    private static SavedQuery Marten(string text, string name = "", string? alias = "person") =>
        new(name, QueryMode.Marten, alias, text);

    [Fact]
    public void A_list_round_trips_through_its_stored_form()
    {
        IReadOnlyList<SavedQuery> original =
        [
            new("Active people", QueryMode.Marten, "person", "where data ->> 'Active' = 'true'"),
            new("Count", QueryMode.Sql, null, "select count(*) from x"),
        ];

        var read = SavedQueryStore.Deserialize(SavedQueryStore.Serialize(original));

        read.Should().BeEquivalentTo(original);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{\"name\":\"an object, not an array\"}")]
    [InlineData("[3, true, null]")]
    [InlineData("[{\"name\":\"no text\"}]")]
    public void Anything_that_is_not_a_list_of_queries_reads_as_no_queries(string? stored)
    {
        SavedQueryStore.Deserialize(stored).Should().BeEmpty();
    }

    [Fact]
    public void An_entry_with_an_unknown_mode_reads_as_the_default_one()
    {
        var read = SavedQueryStore.Deserialize("""[{"name":"x","mode":"telepathy","text":"where 1=1"}]""");

        read.Should().ContainSingle();
        read[0].Mode.Should().Be(QueryMode.Marten);
    }

    [Fact]
    public void The_recent_list_keeps_the_newest_first_and_stops_at_the_cap()
    {
        IReadOnlyList<SavedQuery> recent = [];

        for (var i = 0; i < SavedQueryStore.MaxRecent + 10; i++)
        {
            recent = SavedQueryStore.AddRecent(recent, Marten("where n = " + i));
        }

        recent.Should().HaveCount(SavedQueryStore.MaxRecent);
        recent[0].Text.Should().Be("where n = " + (SavedQueryStore.MaxRecent + 9));
    }

    [Fact]
    public void Running_the_same_query_again_moves_it_up_rather_than_adding_a_second_entry()
    {
        IReadOnlyList<SavedQuery> recent = [];

        recent = SavedQueryStore.AddRecent(recent, Marten("where a = 1"));
        recent = SavedQueryStore.AddRecent(recent, Marten("where b = 2"));
        recent = SavedQueryStore.AddRecent(recent, Marten("where a = 1"));

        recent.Should().HaveCount(2);
        recent[0].Text.Should().Be("where a = 1");
    }

    [Fact]
    public void Saving_under_a_name_that_is_taken_replaces_it_in_place()
    {
        IReadOnlyList<SavedQuery> saved =
        [
            new("First", QueryMode.Marten, "person", "where a = 1"),
            new("Second", QueryMode.Marten, "person", "where b = 2"),
        ];

        saved = SavedQueryStore.Save(saved, new SavedQuery("first", QueryMode.Marten, "person", "where a = 99"));

        saved.Should().HaveCount(2);
        saved[0].Text.Should().Be("where a = 99");
        saved[1].Name.Should().Be("Second");
    }

    [Fact]
    public void A_new_name_goes_to_the_top_and_remove_takes_it_away_again()
    {
        IReadOnlyList<SavedQuery> saved = [new("Old", QueryMode.Sql, null, "select 1")];

        saved = SavedQueryStore.Save(saved, new SavedQuery("New", QueryMode.Sql, null, "select 2"));
        saved[0].Name.Should().Be("New");

        saved = SavedQueryStore.Remove(saved, "new");
        saved.Should().ContainSingle().Which.Name.Should().Be("Old");
    }

    [Fact]
    public void An_unnamed_entry_is_called_after_its_first_line()
    {
        var named = SavedQueryStore.AddRecent([], Marten("select 1\nfrom nowhere"));

        named[0].Name.Should().Be("select 1 …");
    }

    [Fact]
    public void A_query_too_long_to_keep_is_cut_rather_than_refused()
    {
        var huge = new string('x', SavedQueryStore.MaxTextLength + 500);

        var recent = SavedQueryStore.AddRecent([], Marten(huge));

        recent[0].Text.Should().HaveLength(SavedQueryStore.MaxTextLength);
        recent[0].Name.Should().HaveLength(SavedQueryStore.MaxNameLength + 1, "the name is cut and gets an ellipsis");
    }

    [Fact]
    public void Whitespace_is_never_kept_as_a_query()
    {
        SavedQueryStore.AddRecent([], Marten("   ")).Should().BeEmpty();
    }

    /// <summary>
    /// The keys carry the store and the mode, so two stores never share a history and a SQL statement
    /// never turns up in the where-clause list.
    /// </summary>
    [Fact]
    public void The_preference_keys_are_scoped_by_store_and_mode()
    {
        SavedQueryStore.RecentKey("default", QueryMode.Marten).Should().Be("ms_query_recent_default_marten");
        SavedQueryStore.RecentKey("Other", QueryMode.Sql).Should().Be("ms_query_recent_other_sql");
        SavedQueryStore.SavedKey(null, QueryMode.Sql).Should().Be("ms_query_saved_default_sql");

        SavedQueryStore.RecentKey("default", QueryMode.Marten)
            .Should().NotBe(SavedQueryStore.SavedKey("default", QueryMode.Marten));
    }
}
