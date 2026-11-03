using MartenStudio.Tests.Conventions;

using PostgresStudio.Services.Connection;
using PostgresStudio.Services.Database;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Health.Checks;

using static PostgresStudio.Tests.Connection.ConnectionTestFacts;

namespace PostgresStudio.Tests.Connection;

/// <summary>
/// UX-FIX-A: the Connection page's defects from the admin validation, held at the model - the comparison's verdict line, the counts and
/// the notice's wording, the stylesheet's section, and the one sentence each of S05's two remedies says.
/// </summary>
public sealed class ConnectionUxFixATests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 9, 0, 0, TimeSpan.Zero);

    private static ConnectionCompareView Compare(string current, string pasted) =>
        ConnectionCompareService.Compare(pasted, Facts(current, Observed()), new FakeEnvironment(), Now);

    // -------------------------------------------------------------------------------------------------
    // The comparison's verdict line
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public void A_pasted_string_that_changes_nothing_in_the_advice_says_so()
    {
        ConnectionCompareView view = Compare("Host=db.example;SSL Mode=Prefer;Username=svc", "Host=db.example;SSL Mode=Prefer;Username=svc");

        view.Verdict.Should().Be("The page's hints are the same for both. Health findings such as S05 or S12 are judged only for the connection in effect.");
    }

    [Fact]
    public void The_verdict_names_the_certificate_check_and_the_readable_password_from_the_two_settings_and_then_the_advice()
    {
        ConnectionCompareView view = Compare(
            "Host=db.example;SSL Mode=Prefer;Persist Security Info=true;Username=svc",
            "Host=db.example;SSL Mode=VerifyFull;Root Certificate=/etc/ssl/ca.pem;Username=svc");

        view.Verdict.Should().StartWith(
            "The pasted settings check the server's certificate (SSL Mode=VerifyFull) and no longer keep the password readable (Persist Security Info).");
        view.Verdict.Should().Contain("No longer applies: ", "the certificate hint is gone with the pasted mode");
        view.Verdict.Should().EndWith(".");
    }

    [Fact]
    public void The_verdict_lists_the_advice_that_is_new_by_its_heading()
    {
        ConnectionCompareView view = Compare("Host=db.example;SSL Mode=VerifyFull;Root Certificate=/etc/ssl/ca.pem", "Host=a.example,b.example;SSL Mode=VerifyFull;Root Certificate=/etc/ssl/ca.pem");

        view.Verdict.Should().StartWith("New advice: ").And.Contain("Writes can land on a standby");
        view.Verdict.Should().NotContain("The pasted settings", "no certificate or password setting changed");
    }

    [Theory]
    [InlineData("VerifyFull", "Prefer", "The pasted settings no longer check the server's certificate (SSL Mode=Prefer)")]
    [InlineData("Prefer", "Disable", "The pasted settings never encrypt the connection (SSL Mode=Disable)")]
    [InlineData("Disable", "Require", "The pasted settings can encrypt the connection (SSL Mode=Require)")]
    public void The_verdict_says_only_what_the_modes_say(string was, string now, string expected)
    {
        ConnectionCompareView view = Compare("Host=db.example;SSL Mode=" + was, "Host=db.example;SSL Mode=" + now);

        view.Verdict.Should().StartWith(expected);
    }

    [Fact]
    public void A_password_that_becomes_readable_is_said_and_a_mode_change_inside_the_same_check_is_not_a_clause()
    {
        ConnectionCompareView view = Compare("Host=db.example;SSL Mode=Prefer", "Host=db.example;SSL Mode=Require;Persist Security Info=true");

        view.Verdict.Should().StartWith("The pasted settings keep the password readable (Persist Security Info).");
    }

    [Fact]
    public void A_verdict_names_at_most_three_headings_and_counts_the_rest()
    {
        List<ConnectionCompareCard> cards =
        [
            .. Enumerable.Range(1, 5).Select(static n => new ConnectionCompareCard(
                new ConnectionCard("conn-cmp-" + n, ConnectionCardKind.Hint, "Heading " + n + ".", "text", [], [], []),
                ConnectionCompareChange.New)),
            new ConnectionCompareCard(new ConnectionCard("conn-cmp-old", ConnectionCardKind.Hint, "Unchanged", "text", [], [], []), ConnectionCompareChange.Unchanged),
        ];
        ConnectionSettings settings = Facts("Host=db.example", Observed()).Settings;

        ConnectionComparer.VerdictOf(settings, settings, cards, ["Gone one"]).Should().Be(
            "New advice: Heading 1; Heading 2; Heading 3 and 2 more. No longer applies: Gone one.");
    }

    [Fact]
    public void A_connection_in_effect_that_could_not_be_read_has_no_verdict()
    {
        Compare("Port=not-a-port", "Host=db.example;Database=app").Verdict.Should().BeNull("there is nothing to compare with");
    }

    // -------------------------------------------------------------------------------------------------
    // Counts and notices
    // -------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(3, 0, 2, "3 findings · 2 notes")]
    [InlineData(1, 2, 2, "1 finding · 2 hints · 2 notes")]
    [InlineData(1, 1, 1, "1 finding · 1 hint · 1 note")]
    [InlineData(0, 3, 1, "3 hints · 1 note")]
    [InlineData(0, 0, 2, "2 notes")]
    [InlineData(0, 0, 1, "1 note")]
    public void The_worth_a_look_count_names_its_findings_hints_and_notes_and_leaves_out_a_kind_with_none(int findings, int hints, int notes, string expected) =>
        ConnectionPageBuilder.CountLabel(findings, hints, notes).Should().Be(expected);

    [Theory]
    [InlineData(1, HealthService.ActivityPolicyRefusal, "1 finding is hidden because your account may not see activity on this database.")]
    [InlineData(3, HealthService.ActivityPolicyRefusal, "3 findings are hidden because your account may not see activity on this database.")]
    [InlineData(2, "needs PostgresStudioOptions.Capabilities.ViewDatabaseActivity", "2 findings are hidden: needs PostgresStudioOptions.Capabilities.ViewDatabaseActivity.")]
    [InlineData(1, null, "1 finding is hidden from your account.")]
    public void The_notice_for_withheld_findings_is_count_aware_and_keeps_a_reason_it_has_no_sentence_for(int count, string? reason, string expected) =>
        ConnectionPageBuilder.WithheldNotice(count, reason).Should().Be(expected);

    [Fact]
    public void C14_names_the_connections_it_counts_in_the_right_number()
    {
        // The count in C14's "why it matters" was "1 connections": one unnamed connection is a connection.
        string one = Unattributed(1);
        string many = Unattributed(2);

        one.Should().Contain("somebody holds 1 connection\"").And.NotContain("1 connections");
        many.Should().Contain("somebody holds 2 connections\"");
    }

    private static string Unattributed(long unnamed)
    {
        SessionHolderGroup group = new("db", string.Empty, "role", "10.1.2.3", false, unnamed, 0, unnamed, 0, 0, TimeSpan.FromMinutes(3), null);
        ConnectionHolderFacts facts = new(
            Now,
            new ServerConnectionLimits(95, 0, 103, 3, 0, 170000),
            new SessionServerHolders(true, 103, 3, 0, 170000, 95, 1, [group]));

        return new UnattributedConnectionsCheck().Evaluate(facts, HealthEvaluation.At(Now)).Findings.Single().WhyItMatters;
    }

    // -------------------------------------------------------------------------------------------------
    // S05's remedy
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public void S05s_remedy_text_is_two_steps_with_ssl_off_and_one_with_it_on()
    {
        string off = UnencryptedConnectionCheck.RemedyText(serverOff: true, "the data source");
        string on = UnencryptedConnectionCheck.RemedyText(serverOff: false, "the data source");

        off.Should().StartWith("1. Enable TLS on the server (ssl = on with a certificate; on a managed platform it is usually on already).\n2. Then set SSL Mode=VerifyFull (and Root Certificate= if the CA is private)");
        on.Should().NotContain("1. Enable TLS");
        on.Should().StartWith("In the connection string the data source is built from - a client setting, not a server one.");

        foreach (string text in new[] { off, on })
        {
            text.Should().Contain("SSL Mode=Prefer or Require encrypts but does not check the server's certificate.");
        }
    }

    // -------------------------------------------------------------------------------------------------
    // The stylesheet's section
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public void The_stylesheet_has_the_ux_fix_a_section_with_the_stacked_cards_below_720_pixels_and_the_column_widths()
    {
        string css = File.ReadAllText(RepositoryRoot.Combine("src", "PostgresStudio", "wwwroot", "css", "studio.css"));
        int start = css.IndexOf("/* ===== 1.1-UX-FIX-A", StringComparison.Ordinal);

        start.Should().BeGreaterThan(0, "D26: the section is appended");
        string section = css[start..];

        section.Should().Contain("@media (max-width: 719px)", "the stylesheet's own 720 px step: a tablet's region would otherwise scroll sideways");
        section.Should().Contain("min-width: 36rem");
        section.Should().NotContain("min-width: 50rem");
        section.Should().Contain("white-space: pre-line");
        section.Should().Contain("content: attr(data-label)");
        section.Should().Contain(".ms-conn-fix .ms-health-remedy-code");
        section.Should().Contain("white-space: pre-wrap");

        foreach (string width in new[] { ".ms-conn-col-setting {\n    width: 26%;", ".ms-conn-col-effect {\n    width: 40%;", ".ms-conn-col-from {\n    width: 18%;", ".ms-conn-col-default {\n    width: 16%;" })
        {
            section.Should().Contain(width);
        }

        // The Compare disclosure's heading takes the scale `.ms-section > h2` gives the page's other titles.
        int heading = section.IndexOf(".ms-conn-compare-summary .ms-section-title", StringComparison.Ordinal);
        section[heading..(heading + 220)].Should().Contain("font-size: var(--ms-font-size-sm)").And.Contain("text-transform: uppercase");
    }
}
