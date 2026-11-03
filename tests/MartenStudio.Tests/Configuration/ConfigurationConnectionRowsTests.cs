using Bunit;

using Marten;

using MartenStudio.Services.Configuration;
using MartenStudio.Tests.Components;
using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using ConfigurationPage = MartenStudio.Components.Pages.Configuration.Configuration;

namespace MartenStudio.Tests.Configuration;

/// <summary>
/// The Configuration page's connection rows (1.1-NP4): the command timeout in honest words, the connection options, the multi-host
/// preferences, the schema lock id, row-level security as "configured" and never its setting, and the two event options Marten 9.57
/// added. Against a real <c>DocumentStore</c> that never connects.
/// </summary>
public class ConfigurationConnectionRowsTests
{
    private const string Remote = "Host=marten-np4.invalid;Port=5432;Database=np4;Username=svc";

    [Fact]
    public void The_store_card_gains_the_connection_rows_each_with_the_member_behind_it()
    {
        using DocumentStore store = DocumentStore.For(options =>
        {
            options.Connection(Remote + ";Command Timeout=77");
            options.DatabaseSchemaName = "np4_config";
            options.UseStickyConnectionLifetimes = true;
            options.DisableNpgsqlLogging = false;
            options.Advanced.HiloSequenceDefaults.MaxLo = 250;
            options.Advanced.HiloSequenceDefaults.MaxAdvanceToNextHiAttempts = 12;
            options.Advanced.MultiHostSettings.ReadSessionPreference = TargetSessionAttributes.PreferStandby;
            options.ApplyChangesLockId = 99;
            options.UseRowLevelSecurity("app.tenant_id");
        });

        StoreConfiguration card = ConfigurationDescriber.DescribeStore("default", "Default", store.Options, null, null, [], null, store.Storage.Database);

        Value(card, "Command timeout").Should().Be("77 s");
        Member(card, "Command timeout").Should().Be("StoreOptions.CommandTimeout", "the member column holds the member path and nothing else");
        Note(card, "Command timeout").Should().Contain("Both say the same, so it does not matter which one Marten reads");
        Value(card, "Command timeout in the connection string").Should().Be("77 s");
        Member(card, "Command timeout in the connection string").Should().Be("IMartenDatabase.ConfiguredCommandTimeout");
        Value(card, "Sticky connection lifetimes").Should().Be("yes");
        Member(card, "Sticky connection lifetimes").Should().Be("StoreOptions.UseStickyConnectionLifetimes");
        Value(card, "Npgsql logging disabled").Should().Be("no");
        Member(card, "Npgsql logging disabled").Should().Be("StoreOptions.DisableNpgsqlLogging (data sources Marten builds)");
        Note(card, "Npgsql logging disabled").Should().Contain("Applies only to a data source Marten builds");
        Value(card, "Connection tracing").Should().Be("None");
        Member(card, "Connection tracing").Should().Be("StoreOptions.OpenTelemetry.TrackConnections");
        Value(card, "Hi-lo block size").Should().Be("250");
        Value(card, "Hi-lo attempts to advance").Should().Be("12");
        Member(card, "Hi-lo attempts to advance").Should().Be("StoreOptions.Advanced.HiloSequenceDefaults.MaxAdvanceToNextHiAttempts");
        Value(card, "Read session preference").Should().Be("PreferStandby");
        Value(card, "Write session preference").Should().Be("ReadWrite");
        Member(card, "Write session preference").Should().Be("StoreOptions.Advanced.MultiHostSettings.WriteSessionPreference");
        Value(card, "Schema apply lock id").Should().Be("99");
        Member(card, "Schema apply lock id").Should().Be("StoreOptions.ApplyChangesLockId");

        ConfigurationValue rls = card.Connections.Single(static x => x.Name == "Row-level security");
        rls.Value.Should().Be("configured", "the setting's name is the host's, and the page says only that one is set");
        rls.Member.Should().NotContain("tenant_id");
        rls.Note.Should().NotContain("tenant_id");

        card.Connections.Should().OnlyContain(static x => x.Member.Length > 0);

        // F8: a member is a path (at most with a short qualifier), never a sentence.
        card.Connections.Select(static x => x.Member).Should().OnlyContain(
            static member => !member.Contains(',', StringComparison.Ordinal) && !member.Contains(':', StringComparison.Ordinal) && !member.Contains(" wins", StringComparison.Ordinal));
    }

    [Fact]
    public void A_command_timeout_that_is_not_the_default_says_who_set_it_and_that_it_wins_in_the_note()
    {
        using DocumentStore store = DocumentStore.For(static options =>
        {
            options.Connection(Remote + ";Command Timeout=77");
            options.CommandTimeout = 12;
            options.DatabaseSchemaName = "np4_config";
        });

        StoreConfiguration card = ConfigurationDescriber.DescribeStore("default", "Default", store.Options, null, null, [], null, store.Storage.Database);

        Value(card, "Command timeout").Should().Be("12 s");
        Note(card, "Command timeout").Should().Contain("so it was set, by the application or by Connection(string) / UseNpgsqlDataSource()")
            .And.Contain("(Npgsql's 30 s when the string names none), and it wins");
    }

    [Fact]
    public void A_default_timeout_is_marten_s_default_or_the_same_number_set_on_purpose()
    {
        using DocumentStore store = DocumentStore.For(static options =>
        {
            options.Connection(Remote);
            options.CommandTimeout = 5;
            options.DatabaseSchemaName = "np4_config";
        });

        StoreConfiguration card = ConfigurationDescriber.DescribeStore("default", "Default", store.Options, null, null, [], null, store.Storage.Database);

        Value(card, "Command timeout").Should().Be("5 s");
        Note(card, "Command timeout").Should().Contain("5 s: Marten's default, or the same number set on purpose")
            .And.Contain("so either way 5 s, not Npgsql's 30 s")
            .And.NotContain("nothing sets it", "it may have been set on purpose");
    }

    [Fact]
    public void Row_level_security_that_is_not_set_is_not_configured()
    {
        using DocumentStore store = DocumentStore.For(static options =>
        {
            options.Connection(Remote);
            options.DatabaseSchemaName = "np4_config";
        });

        StoreConfiguration card = ConfigurationDescriber.DescribeStore("default", "Default", store.Options, null, null, [], null, store.Storage.Database);

        Value(card, "Row-level security").Should().Be("not configured");
        Value(card, "Command timeout").Should().Be("30 s", "Connection(string) stores the string's timeout, or Npgsql's 30, into the option");
        Value(card, "Command timeout in the connection string").Should().Be("not named");
    }

    [Fact]
    public void Without_a_database_the_connection_strings_timeout_is_not_reported_and_never_guessed()
    {
        using DocumentStore store = DocumentStore.For(static options =>
        {
            options.Connection(Remote + ";Command Timeout=77");
            options.DatabaseSchemaName = "np4_config";
        });

        StoreConfiguration card = ConfigurationDescriber.DescribeStore("default", "Default", store.Options, null, null, [], null);

        Value(card, "Command timeout in the connection string").Should().Be("not reported");
        Value(card, "Command timeout").Should().Be("77 s");
        Note(card, "Command timeout").Should().Contain("It is not Marten's default of 5 s").And.Contain("and it wins");
    }

    [Fact]
    public void Without_a_database_a_default_timeout_claims_nothing_about_the_connection_string()
    {
        using DocumentStore store = DocumentStore.For(static options =>
        {
            options.Connection(Remote);
            options.CommandTimeout = 5;
            options.DatabaseSchemaName = "np4_config";
        });

        StoreConfiguration card = ConfigurationDescriber.DescribeStore("default", "Default", store.Options, null, null, [], null);

        Note(card, "Command timeout").Should().Be("Marten's default is 5 s; the connection string's Command Timeout was not read.");
    }

    [Fact]
    public void The_event_store_card_gains_the_two_options_marten_9_57_added()
    {
        using DocumentStore store = DocumentStore.For(static options =>
        {
            options.Connection(Remote);
            options.DatabaseSchemaName = "np4_config";
            options.Events.UseIdentityMapForAggregates = false;
            options.Events.AutoDiscoverTagTypes = false;
        });

        IReadOnlyStoreOptions readOnly = store.Options;
        EventStoreConfiguration events = ConfigurationDescriber.DescribeEvents(readOnly.Events);

        ConfigurationValue identityMap = events.Values.Single(static x => x.Name == "Identity map for aggregates");
        identityMap.Value.Should().Be("no");
        identityMap.Member.Should().Be("StoreOptions.Events.UseIdentityMapForAggregates");

        ConfigurationValue tags = events.Values.Single(static x => x.Name == "Auto-discover tag types");
        tags.Value.Should().Be("no");
        tags.Member.Should().Be("StoreOptions.Events.AutoDiscoverTagTypes");
    }

    [Fact]
    public async Task The_page_draws_a_connections_and_sessions_group_that_links_to_how_marten_uses_the_connection()
    {
        using DocumentStore store = DocumentStore.For(static options =>
        {
            options.Connection(Remote + ";Command Timeout=77");
            options.DatabaseSchemaName = "np4_config";
        });

        FakeConfigurationService fake = new();
        fake.Configuration = fake.Configuration with
        {
            Store = fake.Configuration.Store with
            {
                Connections = ConfigurationDescriber.DescribeConnections((StoreOptions) store.Options, store.Storage.Database),
            },
        };

        await using StudioComponentContext context = new(services => services.AddSingleton<IConfigurationService>(fake));
        context.WithStores("default");

        IRenderedComponent<ConfigurationPage> page = context.Render<ConfigurationPage>();

        page.WaitForAssertion(() => page.FindAll("#config-connections .ms-config-row").Should().HaveCount(11));
        page.Markup.Should().Contain("Connections and sessions");
        page.Find("#config-connections").TextContent.Should().Contain("Command timeout").And.Contain("77 s").And.Contain("StoreOptions.UseStickyConnectionLifetimes");

        page.Find("a[href*='database/connection']").GetAttribute("href").Should().Contain("#conn-marten");

        // The explanation is drawn under the member, with the link to the section, and the member column stays a path.
        page.FindAll("#config-connections .ms-config-note a").Should().NotBeEmpty()
            .And.OnlyContain(static a => a.GetAttribute("href")!.Contains("#conn-marten", StringComparison.Ordinal));
        page.Find("#config-connections .ms-config-note").TextContent.Should().Contain("Both say the same");

        // The link is on the timeout and logging rows only: the section says nothing more about row-level security.
        page.FindAll("#config-connections .ms-config-row").Where(static row => row.QuerySelector(".ms-config-note a") is not null)
            .Select(static row => row.QuerySelector(".ms-config-name")!.TextContent)
            .Should().BeEquivalentTo("Command timeout", "Command timeout in the connection string", "Npgsql logging disabled");
        page.FindAll("#config-connections .ms-config-row").Single(static row => row.QuerySelector(".ms-config-name")!.TextContent == "Row-level security")
            .QuerySelector(".ms-config-note")!.TextContent.Should().Contain("never shown here");
        page.FindAll("#config-connections .ms-config-member").Select(static x => x.TextContent)
            .Should().OnlyContain(static member => !member.Contains("wins", StringComparison.Ordinal) && !member.Contains(',', StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_store_whose_options_are_not_marten_s_own_shows_no_group_rather_than_an_empty_one()
    {
        FakeConfigurationService fake = new();

        await using StudioComponentContext context = new(services => services.AddSingleton<IConfigurationService>(fake));
        context.WithStores("default");

        IRenderedComponent<ConfigurationPage> page = context.Render<ConfigurationPage>();
        page.WaitForAssertion(() => page.Markup.Should().Contain("Document schema"));

        page.Markup.Should().NotContain("Connections and sessions");
    }

    private static string Value(StoreConfiguration card, string name) => Row(card, name).Value;

    private static string Member(StoreConfiguration card, string name) => Row(card, name).Member;

    private static string Note(StoreConfiguration card, string name) => Row(card, name).Note ?? throw new InvalidOperationException($"The row '{name}' has no note.");

    private static ConfigurationValue Row(StoreConfiguration card, string name) =>
        card.Connections.SingleOrDefault(x => x.Name == name)
            ?? throw new InvalidOperationException($"No connection row named '{name}'. There were: {string.Join(", ", card.Connections.Select(static x => x.Name))}");
}
