using System.Globalization;

using Marten.Schema;

using MartenStudio.Internal.Sql;
using MartenStudio.Services.Configuration;
using MartenStudio.Services.Health;
using MartenStudio.Services.Health.Checks;

using MartenStudio.Tests.Sql;

using PostgresStudio.Services.Health;
using PostgresStudio.Services.Schema;

namespace MartenStudio.Tests.Health;

/// <summary>A document type a hidden-type test names.</summary>
public sealed class DotlessInvoice
{
    public Guid Id { get; set; }
}

/// <summary>A document type with a literal dotless <c>ı</c> in its own name: Marten keeps it at every version.</summary>
public sealed class Kayıt
{
    public Guid Id { get; set; }
}

/// <summary>
/// 0.10 V1's upgrade-hazard check at its edges: K09, document storage named with a dotless <c>ı</c> that Marten 9.55 no
/// longer writes. (K10, a schema name Weasel before 9.47.1 could not apply twice, was retired in 1.1: Marten 9.57 needs a
/// Weasel that can.)
/// </summary>
public class UpgradeHazardChecksTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

    private static HealthEvaluation At(string? marten = "9.57.0") =>
        HealthEvaluation.At(Now, MartenRuntime.ForVersions(marten is null ? [] : new Dictionary<RuntimePackage, string> { [RuntimePackage.Marten] = marten }));

    // ---- K09 ------------------------------------------------------------------------------------------------------------

    private const string Dotless = "ı";

    private static DotlessName Table(string name, bool twin = false, IReadOnlyList<Type>? types = null, bool listable = true) =>
        new(false, "shop", name, twin, types ?? [], listable);

    private static DotlessName Function(string name, bool twin = false, IReadOnlyList<Type>? types = null, bool listable = true) =>
        new(true, "shop", name, twin, types ?? [], listable);

    private static HealthVerdict Judge(params DotlessName[] names) =>
        new DotlessDocumentNamesCheck().Evaluate(new DotlessDocumentNamesFacts(names, false, 1), At()).ShouldBeWellFormedFor<DotlessDocumentNamesCheck>();

    [Fact]
    public void A_table_with_no_invariant_twin_is_dotless_only_and_its_documents_appear_missing()
    {
        HealthFinding finding = Judge(Table("mt_doc_" + Dotless + "nvoice")).Findings.Should().ContainSingle().Subject;

        finding.Code.Should().Be("K09");
        finding.Variant.Should().Be("dotless-only");
        finding.Severity.Should().Be(HealthSeverity.Warning);
        finding.Subject.Should().Be("shop.mt_doc_" + Dotless + "nvoice");
        finding.Title.Should().Contain("this Marten looks for the invariant name").And.Contain("its documents appear missing");
        finding.Title.Should().NotContain("upgrading");
        finding.WhyItMatters.Should().Contain("creates it empty").And.NotContain("After the upgrade");
        finding.Evidence.Should().Contain(x => x.Label == "Invariant name" && x.Value == "mt_doc_invoice (does not exist)");
        finding.Remedy!.Code.Should().Contain("With the application stopped").And.NotContain("Before upgrading");
        finding.Remedy.Explanation.Should().Contain("the studio renames and drops nothing");
    }

    [Fact]
    public void A_table_whose_twin_exists_is_both_and_the_documents_are_split()
    {
        HealthFinding finding = Judge(Table("mt_doc_" + Dotless + "nvoice", twin: true)).Findings.Should().ContainSingle().Subject;

        finding.Variant.Should().Be("both");
        finding.Title.Should().Contain("split between two tables");
        finding.Evidence.Should().Contain(x => x.Label == "Invariant name" && x.Value == "mt_doc_invoice (exists too)");
    }

    [Fact]
    public void A_document_types_functions_are_evidence_of_its_tables_finding()
    {
        HealthVerdict verdict = Judge(
            Table("mt_doc_" + Dotless + "nvoice"),
            Function("mt_upsert_" + Dotless + "nvoice"),
            Function("mt_insert_" + Dotless + "nvoice"),
            Function("mt_update_" + Dotless + "nvoice"),
            Function("mt_overwrite_" + Dotless + "nvoice"));

        HealthFinding finding = verdict.Findings.Should().ContainSingle().Subject;
        HealthEvidence functions = finding.Evidence.Single(static x => x.Label == "Functions");
        functions.Value.Should().Contain("mt_upsert_" + Dotless + "nvoice").And.Contain("mt_overwrite_" + Dotless + "nvoice");
    }

    [Fact]
    public void A_function_with_no_dotless_table_is_a_finding_of_its_own_with_its_own_variant()
    {
        HealthVerdict verdict = Judge(
            Function("mt_upsert_" + Dotless + "nvoice_summary", twin: true),
            Function("mt_upsert_" + Dotless + "tem"));

        verdict.Findings.Should().HaveCount(2);
        verdict.Findings.Single(static x => x.Variant == "both").Title.Should().StartWith("The function shop.mt_upsert_" + Dotless + "nvoice_summary exists under");
        verdict.Findings.Single(static x => x.Variant == "dotless-only").Title.Should().Contain("this Marten looks for the invariant name");
    }

    [Fact]
    public void A_finding_carries_the_document_type_so_the_sweep_drops_it_while_the_host_hides_the_type()
    {
        HealthFinding finding = Judge(Table("mt_doc_" + Dotless + "nvoice", types: [typeof(DotlessInvoice)])).Findings.Single();

        finding.DocumentTypes.Should().Equal(typeof(DotlessInvoice));
    }

    [Fact]
    public void A_name_the_host_may_hide_is_counted_per_variant_and_never_named()
    {
        HealthVerdict verdict = Judge(
            Table("mt_doc_" + Dotless + "nvoice", listable: false),
            Function("mt_upsert_" + Dotless + "nvoice", listable: false),
            Table("mt_doc_" + Dotless + "tem", twin: true, listable: false));

        verdict.Findings.Should().HaveCount(2);
        HealthFinding dotlessOnly = verdict.Findings.Single(static x => x.Variant == "dotless-only");
        dotlessOnly.Subject.Should().Be(DotlessDocumentNamesCheck.Unlisted);
        dotlessOnly.Title.Should().StartWith("2 tables or functions are named with a dotless");
        dotlessOnly.Evidence.Should().Contain(x => x.Label == "Tables" && x.Value == "1");
        dotlessOnly.Evidence.Should().Contain(x => x.Label == "Functions" && x.Value == "1");

        string text = string.Join(
            " ",
            verdict.Findings.SelectMany(static x => new[] { x.Title, x.WhyItMatters, x.Caveat, x.Subject }.Concat(x.Evidence.Select(static e => e.Value))));
        text.Should().NotContain("nvoice", "it may be a type the host hides").And.NotContain("mt_doc_").And.NotContain("mt_upsert_");
    }

    [Fact]
    public void A_read_that_stopped_says_so_and_nothing_found_is_quiet()
    {
        HealthFinding finding = new DotlessDocumentNamesCheck()
            .Evaluate(new DotlessDocumentNamesFacts([Table("mt_doc_" + Dotless + "nvoice")], true, 1), At()).Findings.Single();
        finding.Evidence.Should().Contain(x => x.Label == "More");

        new DotlessDocumentNamesCheck().Evaluate(new DotlessDocumentNamesFacts([], false, 1), At()).Findings.Should().BeEmpty();
        new DotlessDocumentNamesCheck().Evaluate(new DotlessDocumentNamesFacts([], false, 0), At()).Note.Should().Contain("no schema");
    }

    [Fact]
    public void The_statement_binds_the_letter_and_the_schemas_and_holds_no_identifier()
    {
        PostgresStudio.Internal.Sql.HealthQuery query = DotlessNameQueries.Rows(["shop"]);

        query.Sql.Should().NotContain("shop").And.NotContain(Dotless).And.NotContain("mt_");
        query.Parameters.Select(static x => x.ParameterName).Should().BeEquivalentTo("schemas", "prefix", "dotless", "plain", "functionPrefixes", "limit");
        query.Parameters.Single(static x => x.ParameterName == "dotless").Value.Should().Be(Dotless);
    }

    private static Dictionary<string, IDocumentType> Known(string schema = "shop")
    {
        IDocumentType kayit = SqlTestStore.DocumentType<Kayıt>(options => options.Schema.For<Kayıt>().DatabaseSchemaName(schema));
        kayit.TableName.Name.Should().Be("mt_doc_kay" + Dotless + "t", "Marten lowers with ToLowerInvariant, which leaves a literal dotless letter alone");
        return new Dictionary<string, IDocumentType> { [SchemaKey.For(schema, kayit.TableName.Name)] = kayit };
    }

    private static DotlessNameRow Row(bool function, string name, bool twin = false) => new(function, "shop", name, twin);

    [Fact]
    public void A_table_and_its_functions_a_known_type_names_with_a_literal_dotless_i_are_healthy_where_Marten_writes_them_itself()
    {
        DotlessNameRow[] rows =
        [
            Row(false, "mt_doc_kay" + Dotless + "t"),
            Row(true, "mt_upsert_kay" + Dotless + "t"),
            Row(true, "mt_overwrite_kay" + Dotless + "t"),
            Row(false, "mt_doc_" + Dotless + "nvoice"),
        ];

        IReadOnlyList<DotlessName> judged = DotlessDocumentNamesCheck.NamesToJudge(rows, Known(), hidesTypes: false);

        judged.Should().ContainSingle().Which.Name.Should().Be("mt_doc_" + Dotless + "nvoice", "only the name no known type claims is judged");

        HealthVerdict verdict = Judge([.. judged]);
        verdict.Findings.Should().ContainSingle().Which.Caveat.Should().Contain("Kay" + Dotless + "t").And.Contain("rename only where the " + Dotless + " came from a capital I");
    }

    [Fact]
    public void A_healthy_table_a_known_type_names_with_a_literal_dotless_i_is_left_alone_whatever_the_culture_and_whether_the_Marten_version_can_be_read()
    {
        // Every supported Marten writes the exact name, so neither a Turkish process culture nor a Marten whose version cannot be
        // read may send the table to the pre-9.55 path: that would flag a healthy Kayıt and recommend a rename that loses its documents.
        DotlessNameRow[] rows = [Row(false, "mt_doc_kay" + Dotless + "t"), Row(true, "mt_upsert_kay" + Dotless + "t")];
        CultureInfo original = CultureInfo.CurrentCulture;

        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
            CultureInfo.CurrentCulture.TextInfo.ToLower('I').Should().Be(Dotless[0], "the culture is the premise of the case");

            IReadOnlyList<DotlessName> judged = DotlessDocumentNamesCheck.NamesToJudge(rows, Known(), hidesTypes: false);

            judged.Should().BeEmpty();
            new DotlessDocumentNamesCheck().Evaluate(new DotlessDocumentNamesFacts(judged, false, 1), At(marten: null)).Findings.Should().BeEmpty();
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void A_function_with_no_known_table_is_judged_whatever_the_culture()
    {
        DotlessNameRow[] rows = [Row(true, "mt_upsert_" + Dotless + "nvoice"), Row(true, "mt_something_kay" + Dotless + "t")];

        DotlessDocumentNamesCheck.NamesToJudge(rows, Known(), hidesTypes: false).Should().HaveCount(2);
    }

    [Fact]
    public void The_remedy_drops_and_recreates_the_functions_and_never_says_to_rename_them()
    {
        HealthFinding finding = Judge(Table("mt_doc_" + Dotless + "nvoice")).Findings.Should().ContainSingle().Subject;

        finding.Remedy!.Code.Should().Contain("rename each table").And.Contain("drop the dotless functions").And.Contain("apply the schema");
        finding.Remedy.Code.Should().NotContain("rename each table and each of its functions");
        finding.Remedy.Explanation.Should().Contain("Do not rename the functions");
    }

    [Fact]
    public void An_object_knows_its_invariant_name_and_its_document_table()
    {
        Table("mt_doc_" + Dotless + "nvoice").Invariant.Should().Be("mt_doc_invoice");
        Function("mt_upsert_" + Dotless + "nvoice").DocumentTable.Should().Be("mt_doc_" + Dotless + "nvoice");
        Function("mt_something_" + Dotless + "nvoice").DocumentTable.Should().BeNull();
        Table("mt_doc_" + Dotless + "nvoice").DocumentTable.Should().BeNull();
    }
}
