using PostgresStudio.Services.Connection;

using static PostgresStudio.Tests.Connection.ConnectionTestFacts;

namespace PostgresStudio.Tests.Connection;

/// <summary>
/// 1.1-PASTE: a pasted string set beside the one in effect - what differs, what does not, what the advice would be, and what
/// stops applying. The comparer is pure over redacted records, so every test states both sides outright.
/// </summary>
public sealed class ConnectionComparerTests
{
    private const string InEffect = "Host=db.example;Database=app;Username=svc;SSL Mode=Prefer;Maximum Pool Size=100;Application Name=billing";

    private static ConnectionCompareView Compare(string current, string pasted, FakeEnvironment? environment = null)
    {
        ConnectionFacts facts = Facts(current, Observed(), environment: environment);
        PastedConnection read = PastedConnectionString.Read(pasted, new ConnectionReadContext { Environment = environment ?? new FakeEnvironment(), Observed = facts.Observed });

        read.Outcome.Should().Be(PastedConnectionOutcome.Read);

        return ConnectionComparer.Compare(facts, read, new DateTimeOffset(2026, 11, 3, 9, 0, 0, TimeSpan.Zero));
    }

    private static ConnectionCompareRow Row(ConnectionCompareView view, string keyword) => view.Rows.Single(x => x.Keyword == keyword);

    [Fact]
    public void A_setting_that_differs_is_flagged_and_one_that_does_not_is_not()
    {
        ConnectionCompareView view = Compare(InEffect, "Host=db.example;Database=app;Username=svc;SSL Mode=VerifyFull;Maximum Pool Size=50;Application Name=billing");

        view.Outcome.Should().Be(ConnectionCompareOutcome.Compared);
        Row(view, "SSL Mode").Differs.Should().BeTrue();
        Row(view, "SSL Mode").Current!.Value.Should().Be("Prefer");
        Row(view, "SSL Mode").Pasted.Value.Should().Be("VerifyFull");
        Row(view, "Maximum Pool Size").Differs.Should().BeTrue();
        Row(view, "Application Name").Differs.Should().BeFalse();
        Row(view, "Timeout").Differs.Should().BeFalse();
        view.DifferenceCount.Should().Be(2);
    }

    [Fact]
    public void A_setting_spelled_out_at_the_default_is_not_a_difference_however_it_got_there()
    {
        ConnectionCompareView view = Compare("Host=db.example", "Host=db.example;Maximum Pool Size=100;Timeout=15");

        Row(view, "Maximum Pool Size").Differs.Should().BeFalse("100 is Npgsql's default either way");
        Row(view, "Maximum Pool Size").Pasted.Origin.Should().Be(ConnectionSettingOrigin.Pasted);
        Row(view, "Maximum Pool Size").Current!.Origin.Should().Be(ConnectionSettingOrigin.Default);
        Row(view, "Timeout").Differs.Should().BeFalse();
        view.DifferenceCount.Should().Be(0);
    }

    [Fact]
    public void The_environment_a_setting_falls_back_to_is_the_same_on_both_sides()
    {
        ConnectionCompareView view = Compare("Host=db.example", "Host=db.example", new FakeEnvironment(("PGTZ", "Europe/Helsinki")));

        Row(view, "Timezone").Current!.Value.Should().Be("Europe/Helsinki");
        Row(view, "Timezone").Pasted.Value.Should().Be("Europe/Helsinki");
        Row(view, "Timezone").Differs.Should().BeFalse();
    }

    [Fact]
    public void A_password_is_never_compared_and_a_removed_one_is_only_said_to_have_been_there()
    {
        ConnectionCompareView view = Compare(InEffect, InEffect + ";Password=S3NT1NEL;SSL Password=S3NT1NEL");

        Row(view, "Password").Differs.Should().BeFalse("the data source's own cannot be seen, so there is nothing to compare");
        Row(view, "Password").Pasted.Value.Should().Be("set");
        Row(view, "SSL Password").Differs.Should().BeTrue("the data source has none and the text had one");
        view.RemovedSecrets.Should().Equal("Password", "SSL Password");
    }

    [Fact]
    public void Options_are_compared_by_the_names_they_set()
    {
        Compare("Host=db.example;Options=-c search_path=a", "Host=db.example;Options=-c search_path=b").Rows.Single(static x => x.Keyword == "Options").Differs
            .Should().BeFalse("a pasted string's option values are never shown, so the names are what can be compared");

        Compare("Host=db.example;Options=-c search_path=a", "Host=db.example;Options=-c statement_timeout=5").Rows.Single(static x => x.Keyword == "Options").Differs
            .Should().BeTrue();

        Compare("Host=db.example", "Host=db.example;Options=-c search_path=a").Rows.Single(static x => x.Keyword == "Options").Differs
            .Should().BeTrue();
    }

    [Fact]
    public void Advice_that_stops_applying_is_listed_and_advice_the_pasted_settings_bring_is_new()
    {
        ConnectionCompareView view = Compare(
            "Host=db.example;SSL Mode=Prefer",
            "Host=db1.example,db2.example;SSL Mode=VerifyFull;Root Certificate=/etc/ssl/ca.pem");

        view.Resolved.Should().Contain(static x => x.Contains("certificate is not verified", StringComparison.OrdinalIgnoreCase), "VerifyFull answers the TLS hint");
        view.Cards.Select(static x => x.Card.Code).Should().NotContain(ConnectionHintIds.Tls);

        ConnectionCompareCard multi = view.Cards.Single(static x => x.Card.Code == ConnectionHintIds.MultiHost);
        multi.Change.Should().Be(ConnectionCompareChange.New);
    }

    [Fact]
    public void Advice_the_connection_in_effect_has_too_is_unchanged()
    {
        ConnectionCompareView view = Compare("Host=a.example,b.example", "Host=a.example,b.example,c.example");

        view.Cards.Single(static x => x.Card.Code == ConnectionHintIds.MultiHost).Change.Should().Be(ConnectionCompareChange.Unchanged);
        view.Resolved.Should().BeEmpty();
    }

    [Fact]
    public void The_advice_is_judged_against_the_pasted_poolers_and_the_connection_in_effect_s_server()
    {
        ConnectionCompareView view = Compare("Host=db.example", "Host=pooler.example;Port=6543;Max Auto Prepare=10");

        view.Cards.Single(static x => x.Card.Code == ConnectionHintIds.Prepare).Change.Should().Be(ConnectionCompareChange.New, "Supabase's transaction-mode port is in the pasted text, with auto-prepare on");
        view.Resolved.Should().NotContain(static x => x.Contains("Auto-preparation", StringComparison.Ordinal), "context that is not advice is never listed as advice that stopped applying");
    }

    [Fact]
    public void Every_id_the_comparison_draws_is_its_own_so_it_never_repeats_one_of_the_pages()
    {
        ConnectionCompareView view = Compare("Host=a,b;SSL Mode=Prefer", "Host=a,b;SSL Mode=Require");

        view.Rows.Select(static x => x.Anchor).Should().OnlyContain(static x => x.StartsWith("conn-cmp-row-", StringComparison.Ordinal)).And.OnlyHaveUniqueItems();
        view.Cards.Concat(view.Notes).Select(static x => x.Card.Id).Should().OnlyContain(static x => x.StartsWith("conn-cmp-conn-card-", StringComparison.Ordinal)).And.OnlyHaveUniqueItems();
        view.Rows.Where(static x => x.AdviceId is not null).Select(static x => x.AdviceId!).Should().OnlyContain(id => view.Cards.Any(c => c.Card.Id == id));
        Row(view, "SSL Mode").AdviceId.Should().NotBeNull();
    }

    [Fact]
    public void When_the_connection_in_effect_cannot_be_read_every_pasted_setting_is_listed_with_nothing_to_compare_to()
    {
        ConnectionCompareView view = Compare("Host=db.example;Not A Keyword=1", "Host=db.example;SSL Mode=VerifyFull");

        view.CurrentReadable.Should().BeFalse();
        view.Rows.Should().OnlyContain(static x => x.Current == null && x.Differs);
        view.Rows.Should().NotBeEmpty();
    }

    [Fact]
    public void Only_a_string_that_was_read_can_be_compared()
    {
        Action act = () => ConnectionComparer.Compare(Facts("Host=db.example"), PastedConnection.Failed(PastedConnectionOutcome.Unreadable), DateTimeOffset.UnixEpoch);

        act.Should().Throw<ArgumentException>();
    }
}
