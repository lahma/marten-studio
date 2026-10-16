using MartenStudio.Services.Database;

namespace MartenStudio.Tests.Database;

/// <summary>
/// Another application's statement as Database › Sessions shows it (acceptance 5, the fast half): every withheld
/// schema masked, no half-written name left at a cut, capped - nothing at all when the withheld schemas cannot be
/// read - and nothing but a note when it names a hidden document type's table, or, while hiding is configured, a
/// document table no store declares.
/// </summary>
public class SessionQueryTextTests
{
    private static readonly IReadOnlySet<string> Withheld = new HashSet<string>(StringComparer.Ordinal) { "hr", "hr_payroll", "HR Data" };

    private static readonly DatabaseObjectClassifier Hiding = DatabaseTestStores.Classifier();

    private static readonly DatabaseObjectClassifier NotHiding = DatabaseTestStores.Classifier(isVisible: null);

    private static SessionStatementText Present(string query, bool truncated = false, IReadOnlySet<string>? withheld = null) =>
        SessionQueryText.Present(query, truncated, withheld ?? Withheld, NotHiding, hidesDocumentTypes: false);

    private static string? Shown(string query, bool truncated = false) => Present(query, truncated).Text;

    [Theory]
    [InlineData("select * from hr.salaries where id = $1", "select * from ‹withheld›.salaries where id = $1")]
    [InlineData("select * from \"hr\".\"salaries\"", "select * from ‹withheld›.\"salaries\"")]
    [InlineData("select * from \"HR Data\".people", "select * from ‹withheld›.people")]
    [InlineData("set search_path to hr, public", "set search_path to ‹withheld›, public")]
    [InlineData("select * from hr_payroll.runs join public.people p on true", "select * from ‹withheld›.runs join public.people p on true")]
    [InlineData("select hr_public from shared.t", "select hr_public from shared.t")]
    public void Every_withheld_schema_is_masked_wherever_the_statement_names_it(string query, string expected)
    {
        Shown(query).Should().Be(expected);
    }

    [Fact]
    public void Nothing_is_shown_when_the_withheld_schemas_cannot_be_read()
    {
        SessionStatementText text = SessionQueryText.Present("select * from hr.salaries", truncated: false, withheld: null, Hiding, hidesDocumentTypes: true);

        text.Text.Should().BeNull("an unmasked statement is never the fallback");
        text.Note.Should().Contain("could not be checked");
    }

    [Fact]
    public void A_statement_the_role_may_not_read_says_so()
    {
        SessionStatementText text = Present("<insufficient privilege>");

        text.Text.Should().BeNull();
        text.Note.Should().Contain("pg_read_all_stats");
    }

    [Theory]
    [InlineData("select * from hr_payr", "select * from …")]
    [InlineData("select * from public.t where name = 'hr_sec", "select * from public.t where name = …")]
    [InlineData("select * from \"HR Da", "select * from …")]
    [InlineData("select * from hr.salar", "select * from ‹withheld›. …")]
    public void A_cut_statement_loses_its_last_half_written_word_before_it_is_masked(string query, string expected)
    {
        // "hr_payr" is the first half of the withheld hr_payroll, which no mask recognises: it must not be shown.
        Shown(query, truncated: true).Should().Be(expected);
    }

    [Fact]
    public void A_statement_that_was_not_cut_keeps_its_last_word()
    {
        Shown("select * from public.people").Should().Be("select * from public.people");
        Present("select 1").Note.Should().BeNull();
    }

    [Fact]
    public void A_long_statement_is_capped_after_masking_and_never_inside_the_mask()
    {
        string query = new string('x', SessionQueryText.DisplayCharacters - 3) + " hr.salaries";

        SessionStatementText text = Present(query);

        text.Text.Should().EndWith(SessionQueryText.Cut);
        text.Text.Should().NotContain("hr.", "the mask was applied before the cap");
        text.Text!.Replace(SessionQueryText.Cut, string.Empty, StringComparison.Ordinal).Length.Should().BeLessThanOrEqualTo(SessionQueryText.DisplayCharacters);
        text.Note.Should().Contain("Cut");
    }

    [Theory]
    [InlineData("abcdef", 3, 3)]
    [InlineData("ab‹withheld›.t", 5, 2)]
    [InlineData("ab‹withheld›.t", 12, 12)]
    [InlineData("short", 10, 5)]
    public void The_cap_never_splits_the_mask_token(string text, int limit, int expected)
    {
        SessionQueryText.SafeCut(text, limit).Should().Be(expected);
    }

    [Fact]
    public void An_empty_withheld_set_masks_nothing()
    {
        Present("select * from hr.salaries", withheld: new HashSet<string>()).Text
            .Should().Be("select * from hr.salaries", "a visitor who may see every schema sees the statement as written");
    }

    [Theory]
    [InlineData("select d.data from studio_db.mt_doc_dbsecret as d where d.data ->> 'Salary' > $1")]
    [InlineData("select d.data from \"studio_db\".\"mt_doc_dbsecret\" as d")]
    [InlineData("select count(*) from mt_doc_dbsecret")]
    [InlineData("insert into studio_db.mt_doc_dbsecret (data, id) values ($1, $2)")]
    public void A_statement_that_names_a_hidden_types_table_is_withheld_with_a_note(string query)
    {
        SessionStatementText text = SessionQueryText.Present(query, truncated: false, new HashSet<string>(), Hiding, hidesDocumentTypes: true);

        text.Text.Should().BeNull("IsDocumentTypeVisible hides a type from everybody, whatever schema it is in");
        text.Note.Should().Be(SessionQueryText.HiddenTypeNote);
    }

    [Theory]
    [InlineData("select d.data from studio_db.mt_doc_dbpayroll as d")]
    [InlineData("select * from studio_db.mt_doc_dbcustomer_acme")]
    [InlineData("select * from \"studio_db\".\"mt_doc_dbcustomer_acme\"")]
    [InlineData("select * from other_schema.mt_doc_dbcustomer")]
    public void While_types_may_be_hidden_a_statement_naming_a_document_table_nobody_declares_is_withheld(string query)
    {
        SessionStatementText text = SessionQueryText.Present(query, truncated: false, new HashSet<string>(), Hiding, hidesDocumentTypes: true);

        text.Text.Should().BeNull("a type Marten has not learned may be a hidden one, and a tenant partition's name is a tenant id");
        text.Note.Should().Be(SessionQueryText.UndeclaredTableNote);

        SessionQueryText.Present(query, truncated: false, new HashSet<string>(), NotHiding, hidesDocumentTypes: false).Text
            .Should().Be(query, "with nothing hidden there is nothing to protect");
    }

    [Theory]
    [InlineData("select d.data, d.id from studio_db.mt_doc_dbcustomer as d where d.id = $1")]
    [InlineData("select count(*) from mt_doc_dbcustomer")]
    [InlineData("select mt_doc_dbcustomer.id from studio_db.mt_doc_dbcustomer")]
    [InlineData("select * from other_db.mt_doc_dbinvoice")]
    public void A_statement_naming_only_declared_visible_tables_is_shown(string query)
    {
        SessionQueryText.Present(query, truncated: false, new HashSet<string>(), Hiding, hidesDocumentTypes: true).Text.Should().Be(query);
    }

    [Fact]
    public void While_types_may_be_hidden_nothing_is_shown_without_every_stores_declarations()
    {
        SessionStatementText text = SessionQueryText.Present("select 1", truncated: false, new HashSet<string>(), classifier: null, hidesDocumentTypes: true);

        text.Text.Should().BeNull("a statement cannot be checked against declarations that could not be read (fail closed)");
        text.Note.Should().Be(SessionQueryText.UnclassifiedNote);

        SessionQueryText.Present("select 1", truncated: false, new HashSet<string>(), classifier: null, hidesDocumentTypes: false).Text
            .Should().Be("select 1", "with no type hidden, the classifier is not what protects a statement");
    }
}
