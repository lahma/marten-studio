using PostgresStudio.Services.Query;

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

    /// <summary>1.2-POLISH: LINQ mode has lists of its own, so a LINQ entry never lands in SQL mode.</summary>
    [Fact]
    public void LINQ_mode_has_its_own_keys()
    {
        SavedQueryStore.RecentKey("default", QueryMode.Linq).Should().Be("ms_query_recent_default_linq");
        SavedQueryStore.SavedKey("Other", QueryMode.Linq).Should().Be("ms_query_saved_other_linq");

        string[] keys = [.. new[] { QueryMode.Marten, QueryMode.Sql, QueryMode.Linq }.SelectMany(mode =>
            new[] { SavedQueryStore.RecentKey("default", mode), SavedQueryStore.SavedKey("default", mode) })];

        keys.Should().OnlyHaveUniqueItems("each mode and each list has its own key");
    }

    /// <summary>A LINQ entry is two texts and a target: the ordering and the archived selector travel with the predicate.</summary>
    [Fact]
    public void A_LINQ_entry_round_trips_with_its_ordering_and_its_archived_selector()
    {
        IReadOnlyList<SavedQuery> original =
        [
            new("Big orders", QueryMode.Linq, "order", "Total > 100", "Total desc"),
            new("Recent events", QueryMode.Linq, "(events)", "Version > 1", "Sequence", "include"),
            new("No ordering", QueryMode.Linq, "order", "Total > 1"),
        ];

        var read = SavedQueryStore.Deserialize(SavedQueryStore.Serialize(original));

        read.Should().Equal(original);
        read[0].OrderBy.Should().Be("Total desc");
        read[1].Archived.Should().Be("include");
        read[2].OrderBy.Should().BeNull();
    }

    /// <summary>
    /// A list with no LINQ entry is written exactly as 1.1 wrote it, and a list 1.1 wrote reads back: the two new keys are written only
    /// when there is a value, and read as <see langword="null" /> when there is none.
    /// </summary>
    [Fact]
    public void The_new_keys_are_written_only_when_present_and_a_1_1_list_reads_back()
    {
        string written = SavedQueryStore.Serialize([new SavedQuery("Count", QueryMode.Sql, null, "select 1")]);

        written.Should().NotContain("orderBy").And.NotContain("archived");

        var read = SavedQueryStore.Deserialize("""[{"name":"x","mode":"Marten","alias":"person","text":"where 1=1"}]""");
        read.Should().ContainSingle().Which.Should().Be(new SavedQuery("x", QueryMode.Marten, "person", "where 1=1"));
    }

    /// <summary>The same predicate with another ordering, or another archived selector, is another query: both stay in the recent list.</summary>
    [Fact]
    public void The_same_predicate_with_another_ordering_is_another_recent_entry()
    {
        var first = new SavedQuery("p", QueryMode.Linq, "order", "Total > 1", "Total");
        var second = first with { OrderBy = "Total desc" };
        var third = first with { OrderBy = null };

        var list = SavedQueryStore.AddRecent(SavedQueryStore.AddRecent(SavedQueryStore.AddRecent([], first), second), third);

        list.Should().HaveCount(3);
        SavedQueryStore.AddRecent(list, first).Select(static x => x.OrderBy).Should().Equal(new string?[] { "Total", null, "Total desc" }, "the same query again moves up");
    }
}
