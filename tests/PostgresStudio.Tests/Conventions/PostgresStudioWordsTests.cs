using System.Reflection;

using Microsoft.Extensions.Logging;

using PostgresStudio.Internal.Sql;
using PostgresStudio.Services;
using PostgresStudio.Services.Health.Checks;
using PostgresStudio.Services.Postgres;
using PostgresStudio.Services.Settings;
using PostgresStudio.Services.Sources;

namespace PostgresStudio.Tests.Conventions;

/// <summary>
/// PostgresStudio says its own words (PS-3b): every sentence it reads from <see cref="StudioText" /> and every log template
/// it writes is free of MartenStudio's option names, configuration and session APIs - except the few that name Marten
/// on purpose, to say that a Marten store's objects are browsed elsewhere. The <c>.Checks</c> part holds every shared
/// health check to the same, by evaluating it (wave 6).
/// </summary>
public partial class PostgresStudioWordsTests
{
    /// <summary>What only MartenStudio's configuration and API are called: never in a PostgresStudio sentence.</summary>
    private static readonly string[] MartenStudioNames =
    [
        "MartenStudioOptions", "MartenStudioCapabilities", "StoreOptions", "AddMartenStudio", "LightweightSession", "Marten-managed",
        "StoreAuthorizationPolicy",
    ];

    /// <summary>What a sentence about a store says: never in a PostgresStudio sentence, but the ones about Marten's objects.</summary>
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
    /// configuration, which Marten Studio's Schema screen compares and a plain data source does not have.
    /// </summary>
    private static readonly string[] NamesStoreOptionsOnPurpose = [nameof(StudioText.MartenTablePartitionParameters)];

    public static TheoryData<string> TextMembers() => [.. StringMembers()];

    [Theory]
    [MemberData(nameof(TextMembers))]
    public void No_sentence_speaks_MartenStudios_words(string member)
    {
        string? text = (string?) typeof(StudioText).GetProperty(member)!.GetValue(PostgresStudioText.Instance);

        if (text is null)
        {
            return;
        }

        foreach (string word in MartenStudioNames)
        {
            if (word == "StoreOptions" && NamesStoreOptionsOnPurpose.Contains(member))
            {
                continue;
            }

            text.Should().NotContain(word, member + " is Postgres Studio's");
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
    /// A Marten table whose partitions disagree on <c>autovacuum_enabled</c> (T07, T08) is pointed at Marten Studio's Schema
    /// screen and its store's <c>StoreOptions</c> - Postgres Studio has no Schema screen of its own to send anybody to - and
    /// a relational table, as in MartenStudio, at the catalog.
    /// </summary>
    [Theory]
    [InlineData("DocumentTable", "mt_doc_order")]
    [InlineData("EventTable", "mt_events")]
    [InlineData("Bookkeeping", "mt_hilo")]
    public void A_Marten_tables_differing_partitions_point_at_Marten_Studios_Schema_screen(string kindName, string name)
    {
        StudioTableKind kind = Enum.Parse<StudioTableKind>(kindName);
        PostgresHealthTableSource.MartenKindOf(name).Should().Be(kind, "this is the kind Postgres Studio's table source gives it");

        string said = Autovacuum.State(Partitioned(kind, name), "autovacuum_vacuum_scale_factor", "0.02", PostgresStudioText.Instance);

        said.Should().Contain("may be switched off for some of them (each partition's pg_class.reloptions says which; Marten Studio's "
            + "Schema screen, over the store whose StoreOptions declare this table, shows which parameters the partitions differ on).");
        said.Should().NotContain("(the Schema screen's", "there is no Schema screen in Postgres Studio");
    }

    [Fact]
    public void A_relational_tables_differing_partitions_point_at_the_catalog_in_either_studio()
    {
        foreach (StudioText text in new[] { PostgresStudioText.Instance, StudioText.Marten })
        {
            Autovacuum.State(Partitioned(StudioTableKind.Relational, "orders"), "autovacuum_vacuum_scale_factor", "0.02", text)
                .Should().Be(" Its partitions do not all have the same autovacuum_enabled setting, so autovacuum may be switched off "
                    + "for some of them (each partition's pg_class.reloptions says which).");
        }
    }

    private static TableFacts Partitioned(StudioTableKind kind, string name) =>
        new(new StudioTable("public", name, kind, StudioTableOwnership.Library) { Library = "Marten" }, new HealthTableRow
        {
            Schema = "public", Table = name, Kind = "p", AutovacuumEnabled = true, DifferingStorageOptions = ["autovacuum_enabled"],
        });

    [Fact]
    public void The_scan_sees_MartenStudios_words_in_MartenStudios_text()
    {
        StringMembers().Select(static x => (string?) typeof(StudioText).GetProperty(x)!.GetValue(StudioText.Marten))
            .Count(static text => text is not null && MartenStudioNames.Concat(StoreWords).Any(text.Contains))
            .Should().BeGreaterThan(10, "otherwise the theory above proves nothing");
    }

    [Fact]
    public void PostgresStudios_settings_answer_its_own_words()
    {
        PostgresStudioSettings settings = PostgresStudioSettings.Of(new PostgresStudioOptions());

        settings.Text.Should().BeSameAs(PostgresStudioText.Instance);
        settings.Text.Product.Should().Be(StudioProduct.PostgresStudio);
        settings.Text.OptionsTypeName.Should().Be(settings.OptionsTypeName);
        settings.Text.ProductName.Should().Be(settings.ProductName);
        settings.Text.OwnSchemaFlag.Should().BeNull("an own schema is just a browsable one; there is no store to flag it as");
    }

    [Fact]
    public void Every_log_template_is_Postgres_Studios_and_keeps_MartenStudios_id()
    {
        LoggerMessageAttribute[] postgres = Templates(typeof(PostgresStudioLog));
        Dictionary<int, LoggerMessageAttribute> marten = Templates(typeof(StudioLog)).ToDictionary(static x => x.EventId);

        postgres.Should().NotBeEmpty();

        foreach (LoggerMessageAttribute template in postgres)
        {
            template.Message.Should().Contain("Postgres Studio").And.NotContain("Marten").And.NotContain(" store ");
            marten.Should().ContainKey(template.EventId, "each is a shared event, in other words");
            template.Level.Should().Be(marten[template.EventId].Level, "the same event is the same severity in either studio");
        }
    }

    private static LoggerMessageAttribute[] Templates(Type type) =>
    [
        .. type.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Select(static x => x.GetCustomAttribute<LoggerMessageAttribute>())
            .OfType<LoggerMessageAttribute>(),
    ];
}
