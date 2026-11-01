using System.Globalization;

using Marten;
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
/// 0.10 V1's two upgrade-hazard checks at their edges: K09, document storage named with a dotless <c>ı</c> that Marten 9.55
/// no longer writes, and K10, a schema name Postgres writes quoted that Weasel before 9.47.1 cannot apply twice.
/// </summary>
public class UpgradeHazardChecksTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

    private static MartenRuntime Runtime(string marten, string? weasel = null)
    {
        Dictionary<RuntimePackage, string> versions = new() { [RuntimePackage.Marten] = marten };

        if (weasel is not null)
        {
            versions[RuntimePackage.WeaselPostgresql] = weasel;
        }

        return MartenRuntime.ForVersions(versions);
    }

    private static HealthEvaluation At(string marten, string? weasel = null) => HealthEvaluation.At(Now, Runtime(marten, weasel));

    // ---- K10 ------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("orders", false)]
    [InlineData("public", false)]
    [InlineData("_private", false)]
    [InlineData("shop2_events", false)]
    [InlineData("name", false)]
    [InlineData("type", false)]
    [InlineData("Orders", true)]
    [InlineData("ORDERS", true)]
    [InlineData("order", true)]
    [InlineData("user", true)]
    [InlineData("select", true)]
    [InlineData("left", true)]
    [InlineData("time", true)]
    [InlineData("2fast", true)]
    [InlineData("my-schema", true)]
    [InlineData("my schema", true)]
    [InlineData("schéma", true)]
    [InlineData("", false)]
    public void A_schema_name_is_quoted_when_quote_ident_would_quote_it(string name, bool quoted) =>
        SchemaNameQuoting.WouldBeQuoted(name).Should().Be(quoted);

    [Fact]
    public void The_quoted_schemas_are_the_document_and_event_schemas_that_need_it_each_once()
    {
        // Marten lowercases every schema name it is given (StoreOptions.DatabaseSchemaName, EventGraph.DatabaseSchemaName), so in a
        // store the names are a reserved word or carry a character outside a to z, 0 to 9 and underscore, never an upper-case letter.
        StoreOptions options = new() { DatabaseSchemaName = "order" };
        options.Events.DatabaseSchemaName = "my-events";
        SchemaNameQuoting.QuotedSchemas(options).Should().Equal("order", "my-events");

        StoreOptions same = new() { DatabaseSchemaName = "Order" };
        SchemaNameQuoting.QuotedSchemas(same).Should().Equal(["order"], "the event schema defaults to the document schema, and is named once");

        StoreOptions plain = new() { DatabaseSchemaName = "orders" };
        plain.Events.DatabaseSchemaName = "orders_events";
        SchemaNameQuoting.QuotedSchemas(plain).Should().BeEmpty();
    }

    [Fact]
    public void A_schema_a_document_type_declares_is_judged_too_and_one_only_a_hidden_type_declares_is_never_named()
    {
        static StoreOptions Configured()
        {
            StoreOptions options = new() { DatabaseSchemaName = "orders" };
            options.Events.DatabaseSchemaName = "orders";
            options.Schema.For<DotlessInvoice>().DatabaseSchemaName("order");
            options.Schema.For<Kayıt>().DatabaseSchemaName("hidden-one");
            return options;
        }

        SchemaNameQuoting.QuotedSchemas(Configured()).Should().Equal("hidden-one", "order");
        SchemaNameQuoting.QuotedSchemas(Configured(), static type => type != typeof(Kayıt)).Should().Equal(["order"], "a schema only a hidden type declares is not named");
        SchemaNameQuoting.QuotedSchemas(Configured(), static _ => false).Should().BeEmpty("every type is hidden and the store-level schemas need no quoting");
    }

    [Theory]
    [InlineData("9.33.0", true)]
    [InlineData("9.46.0", true)]
    [InlineData("9.47.0", true)]
    [InlineData("9.47.1", false)]
    [InlineData("9.48.0", false)]
    public void K10_fires_below_Weasel_9_47_1_only(string weasel, bool fires)
    {
        SchemaNameQuotingFacts facts = new(["my-schema"], Version.Parse(weasel), new Version(9, 39, 0));
        HealthVerdict verdict = new SchemaNameQuotingCheck().Evaluate(facts, At("9.39.0", weasel)).ShouldBeWellFormedFor<SchemaNameQuotingCheck>();

        if (!fires)
        {
            verdict.Findings.Should().BeEmpty();
            verdict.Note.Should().Contain("reads the indexes");
            SchemaNameQuoting.Notice(facts.Schemas, facts.Weasel).Should().BeNull();
            return;
        }

        HealthFinding finding = verdict.Findings.Should().ContainSingle().Subject;
        finding.Code.Should().Be("K10");
        finding.Severity.Should().Be(HealthSeverity.Warning);
        finding.Subject.Should().BeNull("it is about the store as a whole");
        finding.Variant.Should().BeNull();
        finding.Title.Should().Contain("\"my-schema\"");
        finding.WhyItMatters.Should().Contain("42P07").And.Contain("Marten 9.55");
        finding.Evidence.Should().Contain(x => x.Label == "Schema" && x.Value == "\"my-schema\"");
        finding.Evidence.Should().Contain(x => x.Label == "Weasel.Postgresql" && x.Value.StartsWith(weasel, StringComparison.Ordinal));
        finding.Remedy.Should().NotBeNull();
        SchemaNameQuoting.Notice(facts.Schemas, facts.Weasel).Should().Contain("42P07").And.Contain("Upgrade to Marten 9.55");
    }

    [Fact]
    public void K10_names_both_schemas_when_both_need_quoting()
    {
        SchemaNameQuotingFacts facts = new(["my-schema", "user"], new Version(9, 33, 0), new Version(9, 39, 0));
        HealthFinding finding = new SchemaNameQuotingCheck().Evaluate(facts, At("9.39.0", "9.33.0")).Findings.Should().ContainSingle().Subject;

        finding.Title.Should().StartWith("The schemas \"my-schema\", \"user\" break");
        finding.Evidence.Should().Contain(x => x.Label == "Schemas" && x.Value == "\"my-schema\", \"user\"");
    }

    [Fact]
    public void K10_is_quiet_with_no_such_name_and_says_when_it_cannot_tell()
    {
        new SchemaNameQuotingCheck().Evaluate(new SchemaNameQuotingFacts([], new Version(9, 33, 0), null), At("9.39.0", "9.33.0"))
            .Findings.Should().BeEmpty();

        HealthVerdict unknown = new SchemaNameQuotingCheck().Evaluate(new SchemaNameQuotingFacts(["my-schema"], null, null), At("9.39.0"));
        unknown.Findings.Should().BeEmpty();
        unknown.Note.Should().Contain("could not be read");
    }

    // ---- K09 ------------------------------------------------------------------------------------------------------------

    private const string Dotless = "ı";

    private static DotlessName Table(string name, bool twin = false, IReadOnlyList<Type>? types = null, bool listable = true) =>
        new(false, "shop", name, twin, types ?? [], listable);

    private static DotlessName Function(string name, bool twin = false, IReadOnlyList<Type>? types = null, bool listable = true) =>
        new(true, "shop", name, twin, types ?? [], listable);

    private static HealthVerdict Judge(string marten, params DotlessName[] names) =>
        new DotlessDocumentNamesCheck().Evaluate(new DotlessDocumentNamesFacts(names, false, 1), At(marten)).ShouldBeWellFormedFor<DotlessDocumentNamesCheck>();

    [Fact]
    public void A_table_with_no_invariant_twin_is_dotless_only_and_before_9_55_is_an_upgrade_warning()
    {
        HealthFinding finding = Judge("9.39.0", Table("mt_doc_" + Dotless + "nvoice")).Findings.Should().ContainSingle().Subject;

        finding.Code.Should().Be("K09");
        finding.Variant.Should().Be("dotless-only");
        finding.Severity.Should().Be(HealthSeverity.Warning);
        finding.Subject.Should().Be("shop.mt_doc_" + Dotless + "nvoice");
        finding.Title.Should().Contain("after upgrading to Marten 9.55").And.Contain("these documents will appear missing");
        finding.Evidence.Should().Contain(x => x.Label == "Invariant name" && x.Value == "mt_doc_invoice (does not exist)");
        finding.Remedy!.Code.Should().Contain("Before upgrading Marten");
        finding.Remedy.Explanation.Should().Contain("the studio renames and drops nothing");
    }

    [Fact]
    public void On_9_55_or_later_the_documents_already_appear_missing()
    {
        HealthFinding finding = Judge("9.56.0", Table("mt_doc_" + Dotless + "nvoice")).Findings.Should().ContainSingle().Subject;

        finding.Title.Should().Contain("this Marten looks for the invariant name").And.Contain("its documents appear missing");
        finding.WhyItMatters.Should().Contain("creates it empty");
    }

    [Fact]
    public void A_table_whose_twin_exists_is_both_and_the_documents_are_split()
    {
        HealthFinding finding = Judge("9.56.0", Table("mt_doc_" + Dotless + "nvoice", twin: true)).Findings.Should().ContainSingle().Subject;

        finding.Variant.Should().Be("both");
        finding.Title.Should().Contain("split between two tables");
        finding.Evidence.Should().Contain(x => x.Label == "Invariant name" && x.Value == "mt_doc_invoice (exists too)");
    }

    [Fact]
    public void A_document_types_functions_are_evidence_of_its_tables_finding()
    {
        HealthVerdict verdict = Judge(
            "9.39.0",
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
            "9.39.0",
            Function("mt_upsert_" + Dotless + "nvoice_summary", twin: true),
            Function("mt_upsert_" + Dotless + "tem"));

        verdict.Findings.Should().HaveCount(2);
        verdict.Findings.Single(static x => x.Variant == "both").Title.Should().StartWith("The function shop.mt_upsert_" + Dotless + "nvoice_summary exists under");
        verdict.Findings.Single(static x => x.Variant == "dotless-only").Title.Should().Contain("Marten will not find it");
    }

    [Fact]
    public void A_finding_carries_the_document_type_so_the_sweep_drops_it_while_the_host_hides_the_type()
    {
        HealthFinding finding = Judge("9.39.0", Table("mt_doc_" + Dotless + "nvoice", types: [typeof(DotlessInvoice)])).Findings.Single();

        finding.DocumentTypes.Should().Equal(typeof(DotlessInvoice));
    }

    [Fact]
    public void A_name_the_host_may_hide_is_counted_per_variant_and_never_named()
    {
        HealthVerdict verdict = Judge(
            "9.39.0",
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
            .Evaluate(new DotlessDocumentNamesFacts([Table("mt_doc_" + Dotless + "nvoice")], true, 1), At("9.39.0")).Findings.Single();
        finding.Evidence.Should().Contain(x => x.Label == "More");

        new DotlessDocumentNamesCheck().Evaluate(new DotlessDocumentNamesFacts([], false, 1), At("9.39.0")).Findings.Should().BeEmpty();
        new DotlessDocumentNamesCheck().Evaluate(new DotlessDocumentNamesFacts([], false, 0), At("9.39.0")).Note.Should().Contain("no schema");
    }

    [Fact]
    public void The_statement_binds_the_letter_and_the_schemas_and_holds_no_identifier()
    {
        PostgresStudio.Internal.Sql.HealthQuery query = DotlessNameQueries.Rows(["shop"]);

        query.Sql.Should().NotContain("shop").And.NotContain(Dotless).And.NotContain("mt_");
        query.Parameters.Select(static x => x.ParameterName).Should().BeEquivalentTo("schemas", "prefix", "dotless", "plain", "functionPrefixes", "limit");
        query.Parameters.Single(static x => x.ParameterName == "dotless").Value.Should().Be(Dotless);
    }

    private static readonly CultureInfo Turkish = new("tr-TR");

    [Theory]
    [InlineData("9.55.0", "tr-TR", true)]
    [InlineData("9.56.0", "en-US", true)]
    [InlineData("9.39.0", "en-US", true)]
    [InlineData("9.39.0", "tr-TR", false)]
    public void Marten_writes_invariant_names_from_9_55_and_before_it_only_under_a_culture_that_lowers_I_to_i(string marten, string culture, bool writes)
    {
        new CultureInfo(culture).TextInfo.ToLower('I').Should().Be(culture == "tr-TR" ? Dotless[0] : 'i', "the culture is the premise of the case");
        DotlessDocumentNamesCheck.WritesInvariantNames(Runtime(marten), new CultureInfo(culture)).Should().Be(writes);
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

        IReadOnlyList<DotlessName> judged = DotlessDocumentNamesCheck.NamesToJudge(rows, Known(), hidesTypes: false, martenWritesInvariant: true);

        judged.Should().ContainSingle().Which.Name.Should().Be("mt_doc_" + Dotless + "nvoice", "only the name no known type claims is judged");

        HealthVerdict verdict = Judge("9.56.0", [.. judged]);
        verdict.Findings.Should().ContainSingle().Which.Caveat.Should().Contain("Kay" + Dotless + "t").And.Contain("rename only where the " + Dotless + " came from a capital I");
    }

    [Fact]
    public void The_same_table_is_judged_where_this_process_lowers_with_a_culture_that_made_the_I_dotless()
    {
        // Marten before 9.55 under tr-TR: the type Kayıt's own name and an Invoice's look alike in the catalog, and what Marten
        // writes now is no promise of what it will write after the upgrade.
        DotlessNameRow[] rows = [Row(false, "mt_doc_kay" + Dotless + "t")];

        DotlessDocumentNamesCheck.NamesToJudge(rows, Known(), hidesTypes: false, martenWritesInvariant: DotlessDocumentNamesCheck.WritesInvariantNames(Runtime("9.39.0"), Turkish))
            .Should().ContainSingle().Which.DocumentTypes.Should().Equal(typeof(Kayıt));
    }

    [Fact]
    public void A_function_with_no_known_table_is_judged_whatever_the_culture()
    {
        DotlessNameRow[] rows = [Row(true, "mt_upsert_" + Dotless + "nvoice"), Row(true, "mt_something_kay" + Dotless + "t")];

        DotlessDocumentNamesCheck.NamesToJudge(rows, Known(), hidesTypes: false, martenWritesInvariant: true).Should().HaveCount(2);
    }

    [Fact]
    public void The_remedy_drops_and_recreates_the_functions_and_never_says_to_rename_them()
    {
        HealthFinding finding = Judge("9.39.0", Table("mt_doc_" + Dotless + "nvoice")).Findings.Should().ContainSingle().Subject;

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
