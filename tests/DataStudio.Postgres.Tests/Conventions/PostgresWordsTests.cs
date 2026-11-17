using System.Reflection;


using DataStudio.Postgres.Internal.Sql;
using DataStudio.Postgres.Services.Health.Checks;
using DataStudio.Postgres.Services.Postgres;
using DataStudio.Postgres.Services.Settings;
using DataStudio.Postgres.Services.Sources;

namespace DataStudio.Postgres.Tests.Conventions;

/// <summary>
/// The Postgres module says its own words (PS-3b): every sentence it reads from <see cref="StudioText" /> is free of the Marten module's
/// option names, configuration and session APIs - except the few that name Marten on purpose, to say that a Marten store's objects are
/// browsed on the Marten pages. The <c>.Checks</c> part holds every shared
/// health check to the same, by evaluating it (wave 6).
/// </summary>
public partial class PostgresWordsTests
{
    /// <summary>What only the Marten module's configuration and API are called: never in a Postgres module sentence.</summary>
    private static readonly string[] MartenNames =
    [
        "MartenOptions", "MartenCapabilities", "StoreOptions", "AddMarten", "LightweightSession", "Marten-managed",
        "StoreAuthorizationPolicy",
    ];

    /// <summary>What a sentence about a store says: never in a Postgres module sentence, but the ones about Marten's objects.</summary>
    private static readonly string[] StoreWords = ["Marten store", "this store", "the store's", "Marten table", "document type", "tenant"];

    private static IEnumerable<string> StringMembers() =>
        typeof(StudioText).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(static x => x.PropertyType == typeof(string))
            .Select(static x => x.Name);

    /// <summary>The members that name Marten deliberately: an object the mt_ rule calls Marten's, and where it is browsed.</summary>
    private static readonly string[] NamesMartenOnPurpose =
    [
        nameof(StudioText.MartenDependencyDenial), nameof(StudioText.MartenInternalTitle), nameof(StudioText.MartenStoreNotice),
        nameof(StudioText.OwnerFilterMartenTitle), nameof(StudioText.OwnerFilterOtherTitle),
        nameof(StudioText.MartenTablePartitionParameters),
    ];

    /// <summary>
    /// The one member that names <c>StoreOptions</c> on purpose: a Marten table's storage parameters are its store's
    /// configuration, which the Marten module's Schema screen compares and a plain data source does not have.
    /// </summary>
    private static readonly string[] NamesStoreOptionsOnPurpose = [nameof(StudioText.MartenTablePartitionParameters)];

    public static TheoryData<string> TextMembers() => [.. StringMembers()];

    [Theory]
    [MemberData(nameof(TextMembers))]
    public void No_sentence_speaks_Marten_words(string member)
    {
        string? text = (string?) typeof(StudioText).GetProperty(member)!.GetValue(PostgresText.Instance);

        if (text is null)
        {
            return;
        }

        foreach (string word in MartenNames)
        {
            if (word == "StoreOptions" && NamesStoreOptionsOnPurpose.Contains(member))
            {
                continue;
            }

            text.Should().NotContain(word, member + " is the Postgres module's");
        }

        if (!NamesMartenOnPurpose.Contains(member))
        {
            text.Should().NotContain("Marten", member + " has no reason to name Marten");

            foreach (string word in StoreWords)
            {
                text.Should().NotContain(word, member + " is about a database, not a store");
            }
        }
    }

    /// <summary>
    /// A Marten table whose partitions disagree on <c>autovacuum_enabled</c> (T07, T08) is pointed at the Marten pages' Schema
    /// screen and its store's <c>StoreOptions</c> - the Postgres module has no Schema screen of its own to send anybody to - and
    /// a relational table at the catalog.
    /// </summary>
    [Theory]
    [InlineData("DocumentTable", "mt_doc_order")]
    [InlineData("EventTable", "mt_events")]
    [InlineData("Bookkeeping", "mt_hilo")]
    public void A_Marten_tables_differing_partitions_point_at_Marten_Studios_Schema_screen(string kindName, string name)
    {
        StudioTableKind kind = Enum.Parse<StudioTableKind>(kindName);
        PostgresHealthTableSource.MartenKindOf(name).Should().Be(kind, "this is the kind the Postgres module's table source gives it");

        string said = Autovacuum.State(Partitioned(kind, name), "autovacuum_vacuum_scale_factor", "0.02", PostgresText.Instance);

        said.Should().Contain("may be switched off for some of them (each partition's pg_class.reloptions says which; the Marten pages' "
            + "Schema screen, over the store whose StoreOptions declare this table, shows which parameters the partitions differ on).");
        said.Should().NotContain("(the Schema screen's", "there is no Schema screen in the Postgres module");
    }

    [Fact]
    public void A_relational_tables_differing_partitions_point_at_the_catalog()
    {
        Autovacuum.State(Partitioned(StudioTableKind.Relational, "orders"), "autovacuum_vacuum_scale_factor", "0.02", PostgresText.Instance)
            .Should().Be(" Its partitions do not all have the same autovacuum_enabled setting, so autovacuum may be switched off "
                + "for some of them (each partition's pg_class.reloptions says which).");
    }

    private static TableFacts Partitioned(StudioTableKind kind, string name) =>
        new(new StudioTable("public", name, kind, StudioTableOwnership.Library) { Library = "Marten" }, new HealthTableRow
        {
            Schema = "public", Table = name, Kind = "p", AutovacuumEnabled = true, DifferingStorageOptions = ["autovacuum_enabled"],
        });

    [Fact]
    public void The_scan_sees_the_Marten_modules_words_in_a_planted_sentence()
    {
        string[] planted =
        [
            "MartenOptions.Capabilities.BrowseDatabase", "StoreOptions.DisableNpgsqlLogging", "the store's own role",
            "a Marten store", "this store", "a document type", "one tenant",
        ];

        planted.Count(static text => MartenNames.Concat(StoreWords).Any(text.Contains)).Should().Be(planted.Length,
            "the words the theory above forbids are the words the Marten module's sentences use, so it can fail");
    }

    [Fact]
    public void Postgres_settings_answer_its_own_words()
    {
        PostgresSettings settings = PostgresSettings.Of(new PostgresOptions());

        settings.Text.Should().BeSameAs(PostgresText.Instance);
        settings.Text.OptionsTypeName.Should().Be(settings.OptionsTypeName);
        settings.Text.OwnSchemaFlag.Should().BeNull("an own schema is just a browsable one; there is no store to flag it as");
    }
}
