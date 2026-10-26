using System.Reflection;

using Microsoft.Extensions.Logging;

using PostgresStudio.Services;
using PostgresStudio.Services.Postgres;
using PostgresStudio.Services.Settings;

namespace PostgresStudio.Tests.Conventions;

/// <summary>
/// PostgresStudio says its own words (PS-3b): every sentence it reads from <see cref="StudioText" /> and every log template
/// it writes is free of MartenStudio's option names, configuration and session APIs - except the few that name Marten
/// on purpose, to say that a Marten store's objects are browsed elsewhere.
/// </summary>
public class PostgresStudioWordsTests
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
    ];

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
