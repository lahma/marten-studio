using AngleSharp.Dom;

using Bunit;

using Confluent.Kafka;

using KStudio.Components.Pages;
using KStudio.Services;
using KStudio.Tests.Support;

namespace KStudio.Tests.Components;

/// <summary>Cluster settings (D-UI decision 8b): what the host handed KStudio, one setting at a time, and what KStudio's clients change.</summary>
public class KafkaSettingsPageTests
{
    private const string Sentinel = "S3NT1NEL-4f9a2c";

    private static async Task<IRenderedComponent<KafkaSettingsPage>> OpenAsync(KafkaCatalogRig rig)
    {
        await rig.Studio.Context.ReadyAsync();
        rig.Studio.Navigation.Simulate("kafka/settings?cluster=settings", intercepted: false);
        return rig.Studio.Context.Render<KafkaSettingsPage>();
    }

    private static KafkaCatalogRig Rig(Action<KafkaEngineOptions> engine) => new(engine, cluster: "settings");

    private static IElement Row(IRenderedComponent<KafkaSettingsPage> page, string table, string key) =>
        page.Find($"[role=region][aria-label=\"{table}\"]").QuerySelectorAll("tbody tr").Single(x => x.QuerySelector("code")?.TextContent == key);

    [Fact]
    public async Task A_secret_reads_set_and_its_value_is_nowhere_in_the_markup()
    {
        await using KafkaCatalogRig rig = Rig(o => o.AddCluster(
            "settings",
            new ClientConfig
            {
                BootstrapServers = "broker-1:9092",
                SecurityProtocol = SecurityProtocol.SaslSsl,
                SaslMechanism = SaslMechanism.Plain,
                SaslUsername = "app",
                SaslPassword = Sentinel,
                SslKeyPem = Sentinel + "-pem",
                SaslOauthbearerClientSecret = Sentinel + "-oauth",
                SslCaLocation = "/nowhere/ca.pem",
            }));

        IRenderedComponent<KafkaSettingsPage> page = await OpenAsync(rig);

        page.WaitForAssertion(() => page.FindAll("[aria-label='Your settings'] tbody tr").Should().NotBeEmpty());
        page.Markup.Should().NotContain("S3NT1NEL");
        Row(page, "Your settings", "sasl.password").TextContent.Should().Contain("set").And.Contain("secret");
        Row(page, "Your settings", "ssl.key.pem").TextContent.Should().Contain("set");
        Row(page, "Your settings", "bootstrap.servers").TextContent.Should().Contain("broker-1:9092");
        Row(page, "Your settings", "ssl.ca.location").TextContent.Should().Contain("/nowhere/ca.pem").And.Contain("path");
        page.Markup.Should().Contain(KafkaStudioText.SettingsLead);
    }

    [Fact]
    public async Task The_capabilities_table_is_the_one_page_that_names_the_option_behind_each_capability()
    {
        await using KafkaCatalogRig rig = new(
            engine: o => o.AddCluster("settings", new ClientConfig { BootstrapServers = "broker-1:9092" }),
            studio: o => o.Capabilities.SearchRecords = true,
            cluster: "settings");

        IRenderedComponent<KafkaSettingsPage> page = await OpenAsync(rig);

        page.WaitForAssertion(() => page.FindAll("[aria-label='Capabilities'] tbody tr").Should().HaveCount(4));
        string[] rows = [.. page.FindAll("[aria-label='Capabilities'] tbody tr").Select(static x => string.Join(" | ", x.QuerySelectorAll("td").Select(static c => c.TextContent.Trim())))];
        rows.Should().Equal(
            "SearchRecords | on | KafkaStudioOptions.Capabilities.SearchRecords",
            "ReadPayloads | off | KafkaStudioOptions.Capabilities.ReadPayloads",
            "ExportRecords | off | KafkaStudioOptions.Capabilities.ExportRecords",
            "LiveTail | off | KafkaStudioOptions.Capabilities.LiveTail");
    }

    [Fact]
    public async Task The_builder_hints_for_the_profile_and_for_consumer_groups_are_on_Settings()
    {
        await using KafkaCatalogRig rig = Rig(o => o.AddCluster("settings", new ClientConfig { BootstrapServers = "broker-1:9092" }));

        IRenderedComponent<KafkaSettingsPage> page = await OpenAsync(rig);

        page.WaitForAssertion(() => page.FindAll(".ms-k-profile-hint").Should().ContainSingle());
        page.Find(".ms-k-profile-hint").TextContent.Should().Contain("KafkaClusterBuilder.Profile").And.Contain("even when they are granted");
        page.Find(".ms-k-groups-hint").TextContent.Should().Contain("KafkaClusterBuilder.ConsumerGroup");
    }

    [Fact]
    public async Task The_overrides_table_lists_D37()
    {
        await using KafkaCatalogRig rig = Rig(o => o.AddCluster("settings", new ClientConfig { BootstrapServers = "broker-1:9092" }));

        IRenderedComponent<KafkaSettingsPage> page = await OpenAsync(rig);

        page.WaitForAssertion(() => page.FindAll("[aria-label=\"What KStudio's clients change\"] tbody tr").Should().NotBeEmpty());
        string[] keys = [.. page.FindAll("[aria-label=\"What KStudio's clients change\"] tbody tr code").Select(static x => x.TextContent)];
        keys.Should().Contain(["enable.auto.commit", "enable.auto.offset.store", "allow.auto.create.topics", "group.id", "isolation.level", "auto.offset.reset"]);
        Row(page, "What KStudio's clients change", "enable.auto.commit").TextContent.Should().Contain("nothing is committed or stored");
        page.Find(".ms-k-oauth").TextContent.Should().Be("OAuth token refresh: not declared");
    }

    [Fact]
    public async Task One_config_has_no_role_column()
    {
        await using KafkaCatalogRig rig = Rig(o => o.AddCluster("settings", new ClientConfig { BootstrapServers = "broker-1:9092" }));

        IRenderedComponent<KafkaSettingsPage> page = await OpenAsync(rig);

        page.WaitForAssertion(() => page.FindAll("[aria-label='Your settings'] tbody tr").Should().NotBeEmpty());
        page.FindAll("[aria-label='Your settings'] thead th").Select(static x => x.TextContent).Should().Equal("Setting", "Value");
    }

    [Fact]
    public async Task Separate_configs_add_the_role_column()
    {
        await using KafkaCatalogRig rig = new(
            o => o.AddCluster(
                "split",
                new ConsumerConfig { BootstrapServers = "consumer-broker:9092", GroupId = "app" },
                new AdminClientConfig { BootstrapServers = "admin-broker:9092" }),
            cluster: "split");
        await rig.Studio.Context.ReadyAsync();
        rig.Studio.Navigation.Simulate("kafka/settings?cluster=split", intercepted: false);

        IRenderedComponent<KafkaSettingsPage> split = rig.Studio.Context.Render<KafkaSettingsPage>();

        split.WaitForAssertion(() => split.FindAll("[aria-label='Your settings'] thead th").Select(static x => x.TextContent).Should().Equal("Setting", "Value", "Role"));
        string[] servers = [.. split.FindAll("[aria-label='Your settings'] tbody tr")
            .Where(static x => x.QuerySelector("code")?.TextContent == "bootstrap.servers")
            .Select(static x => x.TextContent)];
        servers.Should().HaveCount(2, "a key with two values is two rows, each keyed on its role");
        servers.Should().Contain(static x => x.Contains("consumer-broker", StringComparison.Ordinal) && x.Contains("scans", StringComparison.Ordinal));
        servers.Should().Contain(static x => x.Contains("admin-broker", StringComparison.Ordinal) && x.Contains("metadata", StringComparison.Ordinal));
    }
}
