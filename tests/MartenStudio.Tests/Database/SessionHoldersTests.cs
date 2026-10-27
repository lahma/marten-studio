using PostgresStudio.Services.Database;

namespace MartenStudio.Tests.Database;

/// <summary>
/// Database › Sessions › Holders' folds (issue #20), with no database: what a visitor short of <c>ViewDatabaseActivity</c> is
/// shown - this database and the admitted ones by name, every other database together, no application, role or address - what a
/// visitor past it is shown, the budget through C03's one formula, and the reader's mapping of the statement's rows.
/// </summary>
public class SessionHoldersTests
{
    private static SessionHolderGroup Group(
        string database,
        string application,
        long total,
        long idle = 0,
        long active = 0,
        long idleInTransaction = 0,
        long unknown = 0,
        TimeSpan? oldestIdle = null,
        TimeSpan? oldestIdleInTransaction = null,
        bool thisDatabase = false,
        string role = "app_user",
        string? client = "10.0.3.7",
        bool hidden = false) =>
        new(database, application, role, client, hidden, total, active, idle, idleInTransaction, unknown, oldestIdle, oldestIdleInTransaction, thisDatabase);

    private static SessionServerHolders Holders(params SessionHolderGroup[] groups) =>
        new(true, 100, 3, 0, 170000, groups.Sum(static x => x.Total) + 1, groups.Length, groups);

    // ----------------------------------------------------------------------------------------------
    // What a visitor short of the gate is shown
    // ----------------------------------------------------------------------------------------------

    [Fact]
    public void A_visitor_short_of_the_gate_is_given_this_database_and_the_admitted_ones_by_name_and_the_rest_together_unnamed()
    {
        SessionServerHolders holders = Holders(
            Group("app", "billing-api", 10, idle: 6, active: 4, thisDatabase: true, oldestIdle: TimeSpan.FromMinutes(5)),
            Group("app", "psql", 1, idle: 1, thisDatabase: true, oldestIdle: TimeSpan.FromMinutes(1)),
            Group("billing", "billing-api", 30, idle: 25, idleInTransaction: 5, oldestIdle: TimeSpan.FromMinutes(9), oldestIdleInTransaction: TimeSpan.FromSeconds(40)),
            Group("secret", "payroll-sentinel", 20, idle: 12, active: 8, oldestIdle: TimeSpan.FromMinutes(3)),
            Group("analytics", "etl-sentinel", 12, idle: 12, oldestIdle: TimeSpan.FromMinutes(30), oldestIdleInTransaction: TimeSpan.FromMinutes(2)));

        SessionHoldersSummary summary = SessionHolders.Summarize(holders, new HashSet<string>(["billing"], StringComparer.Ordinal));

        // Named databases, the most sessions first.
        summary.Databases.Select(static x => (x.Database, x.Total)).Should().Equal(("billing", 30L), ("app", 11L));
        summary.Databases.Single(static x => x.Database == "app").ThisDatabase.Should().BeTrue();
        summary.Databases.Single(static x => x.Database == "app").Should().BeEquivalentTo(
            new SessionHolderCounts("app", true, 11, 4, 7, 0, 0, TimeSpan.FromMinutes(5), null), "this database's two applications are summed, the oldest idle taken");

        summary.Others.Should().NotBeNull();
        summary.Others!.Database.Should().BeNull("a database the visitor may not be told the name of is counted, never named");
        summary.Others.Total.Should().Be(32, "secret and analytics together");
        summary.Others.Active.Should().Be(8);
        summary.Others.Idle.Should().Be(24);
        summary.Others.OldestIdle.Should().Be(TimeSpan.FromMinutes(30));
        summary.Others.OldestIdleInTransaction.Should().Be(TimeSpan.FromMinutes(2));

        System.Text.Json.JsonSerializer.Serialize(summary).Should()
            .NotContain("secret").And.NotContain("analytics").And.NotContain("payroll-sentinel").And.NotContain("etl-sentinel")
            .And.NotContain("billing-api").And.NotContain("app_user").And.NotContain("10.0.3.7", "no application, role or address is in the summary");
    }

    [Fact]
    public void Nothing_admitted_means_every_other_database_is_one_unnamed_row_and_no_other_database_means_no_such_row()
    {
        SessionServerHolders other = Holders(
            Group("app", "billing-api", 5, idle: 5, thisDatabase: true),
            Group("billing", "billing-api", 7, idle: 7));

        SessionHolders.Summarize(other, new HashSet<string>()).Databases.Select(static x => x.Database).Should().Equal("app");
        SessionHolders.Summarize(other, new HashSet<string>()).Others!.Total.Should().Be(7);

        SessionHoldersSummary alone = SessionHolders.Summarize(Holders(Group("app", "billing-api", 5, idle: 5, thisDatabase: true)), new HashSet<string>());
        alone.Others.Should().BeNull();
    }

    [Fact]
    public void An_admitted_name_that_has_no_sessions_adds_no_row_and_names_are_matched_exactly()
    {
        SessionServerHolders holders = Holders(Group("app", "x", 2, idle: 2, thisDatabase: true), Group("Billing", "x", 4, idle: 4));

        SessionHoldersSummary summary = SessionHolders.Summarize(holders, new HashSet<string>(["billing", "ghost"], StringComparer.Ordinal));

        summary.Databases.Select(static x => x.Database).Should().Equal("app");
        summary.Others!.Total.Should().Be(4, "Postgres' names are case-sensitive: 'Billing' is not 'billing'");
    }

    [Fact]
    public void The_budget_is_the_one_C03_judges_and_the_share_is_of_what_an_ordinary_role_may_open()
    {
        SessionServerHolders holders = new(true, 100, 3, 2, 170000, 76, 1, [Group("app", "x", 75, idle: 75, thisDatabase: true)]);

        SessionHoldersSummary summary = SessionHolders.Summarize(holders, new HashSet<string>());

        summary.ServerConnections.Should().Be(76);
        summary.Available.Should().Be(95, "max_connections less superuser_reserved_connections and reserved_connections");
        summary.AvailableText.Should().StartWith("95 (max_connections 100").And.Contain("2 reserved_connections");
        summary.Share.Should().BeApproximately(0.8, 1e-9);
    }

    [Fact]
    public void Sessions_a_role_cannot_see_into_are_said_and_a_capped_read_is_said()
    {
        SessionServerHolders unreadable = new(
            false, 100, 3, 0, 170000, 12, 2, [Group("app", "x", 4, idle: 4, thisDatabase: true), Group("app", "y", 7, unknown: 7, client: null, hidden: true)]);

        SessionHolders.Summarize(unreadable, new HashSet<string>()).AllSessionsVisible.Should().BeFalse();
        SessionHolders.Summarize(unreadable with { CanReadAllStats = true }, new HashSet<string>()).AllSessionsVisible.Should().BeTrue();
        SessionHolders.Summarize(unreadable with { Groups = [unreadable.Groups[0]] }, new HashSet<string>()).AllSessionsVisible
            .Should().BeTrue("nobody the role cannot read into was connected");

        SessionHoldersSummary capped = SessionHolders.Summarize(unreadable with { GroupCount = 5_000 }, new HashSet<string>());
        capped.Truncated.Should().BeTrue();
        capped.GroupCount.Should().Be(5_000);
        capped.ListedGroups.Should().Be(2);
    }

    // ----------------------------------------------------------------------------------------------
    // What a visitor past the gate is shown
    // ----------------------------------------------------------------------------------------------

    [Fact]
    public void Past_the_gate_every_database_is_named_with_every_group()
    {
        SessionServerHolders holders = Holders(
            Group("app", "billing-api", 10, idle: 6, active: 4, thisDatabase: true),
            Group("secret", "payroll-sentinel", 20, idle: 12, active: 8, role: "hr", client: "10.9.9.9"),
            Group("secret", "payroll-sentinel", 3, unknown: 3, role: "hr", client: null, hidden: true));

        SessionHoldersDetails details = SessionHolders.Detail(holders);

        details.Groups.Should().HaveCount(3);
        details.Databases.Select(static x => (x.Database, x.Total)).Should().Equal(("secret", 23L), ("app", 10L));
        details.Groups[1].ApplicationText.Should().Be("payroll-sentinel");
        details.Groups[1].ClientText.Should().Be("10.9.9.9");
        details.Groups[2].ClientText.Should().Be("not readable", "a role without pg_read_all_stats cannot read the address");
        Group("app", "", 1, client: null).ClientText.Should().Be("local socket");
        Group("app", "", 1).ApplicationText.Should().Be("(none set)");
        details.Truncated.Should().BeFalse();
        SessionHoldersDetails.None.Groups.Should().BeEmpty();
    }

    [Fact]
    public void Ages_are_taken_as_the_oldest_and_nothing_is_an_age_of_nothing()
    {
        SessionHolderCounts.Of("app", false, []).Should().BeEquivalentTo(new SessionHolderCounts("app", false, 0, 0, 0, 0, 0, null, null));

        SessionHolderCounts counts = SessionHolderCounts.Of(
            "app",
            false,
            [Group("app", "a", 1, idle: 1, oldestIdle: TimeSpan.FromSeconds(5)), Group("app", "b", 1, idle: 1), Group("app", "c", 1, idle: 1, oldestIdle: TimeSpan.FromSeconds(50))]);

        counts.OldestIdle.Should().Be(TimeSpan.FromSeconds(50));
        counts.OldestIdleInTransaction.Should().BeNull();
    }

    // ----------------------------------------------------------------------------------------------
    // The reader's mapping
    // ----------------------------------------------------------------------------------------------

    [Fact]
    public void The_rows_are_one_reading_the_settings_once_and_a_group_per_row()
    {
        SessionHolderGroup first = Group("app", "x", 3, idle: 3, thisDatabase: true);
        SessionHolderGroup second = Group("billing", "y", 2, idle: 2);

        SessionServerHolders? reading = SessionActivityReader.Holders(
        [
            new SessionActivityReader.HolderRow(false, 100, 3, 2, 170000, 6, 2, first),
            new SessionActivityReader.HolderRow(false, 100, 3, 2, 170000, 6, 2, second),
        ]);

        reading.Should().NotBeNull();
        reading!.CanReadAllStats.Should().BeFalse();
        reading.MaxConnections.Should().Be(100);
        reading.Reserved.Should().Be(2);
        reading.ServerConnections.Should().Be(6);
        reading.GroupCount.Should().Be(2);
        reading.Groups.Should().Equal(first, second);

        SessionActivityReader.Holders([]).Should().BeNull("a statement that answered nothing at all");

        SessionServerHolders? nobody = SessionActivityReader.Holders([new SessionActivityReader.HolderRow(true, 100, 3, 0, 170000, 1, 0, null)]);
        nobody!.Groups.Should().BeEmpty("the visibility row alone: nobody but the studio's own session is connected");
        nobody.ServerConnections.Should().Be(1);
    }
}
