using System.Reflection;
using System.Text.Json;

using Confluent.Kafka;

using KStudio.Core.Clusters;
using KStudio.Core.Wire;
using KStudio.Core.Wire.Confluent;

namespace KStudio.Tests.Clusters;

/// <summary>
/// The settings reader and its catalogue (D-UI decision 3): every key Confluent declares is classified, a secret is only "set" or "not
/// set", a path is shown and never opened, a key nobody classified is withheld, and the overrides are D37's table read from the configurations
/// the engine builds.
/// </summary>
public class KafkaSettingsReaderTests
{
    private const string Sentinel = "S3NT1NEL-do-not-show";

    private static (KafkaClusterDefinition Definition, KafkaEngineSnapshot Settings) Define(Action<ClientConfig>? configure = null)
    {
        ClientConfig config = new() { BootstrapServers = "broker:9092" };
        configure?.Invoke(config);
        KafkaEngineOptions options = new();
        options.AddCluster("prod", config);
        KafkaClusterRegistry registry = new(options);
        return (registry.Get("prod"), registry.Settings);
    }

    private static KafkaClusterSettingsView Read(Action<ClientConfig>? configure = null)
    {
        (KafkaClusterDefinition definition, KafkaEngineSnapshot settings) = Define(configure);
        return new KafkaSettingsReader(settings).Read(definition);
    }

    private static KafkaSettingRow Row(KafkaClusterSettingsView view, string key) => view.Host.Single(x => x.Key == key);

    /// <summary>The keys the type declares itself: each declared property is set to a sample value and the keys that appear are the declaration.</summary>
    private static HashSet<string> DeclaredKeys<T>()
        where T : Config, new()
    {
        T config = new();

        foreach (PropertyInfo property in typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            if (!property.CanWrite)
            {
                continue;
            }

            Type type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            object? sample = type == typeof(string) ? "x"
                : type == typeof(int) ? 1
                : type == typeof(long) ? 1L
                : type == typeof(bool) ? true
                : type == typeof(double) ? 1.0
                : type.IsEnum ? Enum.GetValues(type).GetValue(0)
                : null;

            if (sample is not null)
            {
                property.SetValue(config, sample);
            }
        }

        return [.. config.Select(static x => x.Key)];
    }

    [Fact]
    public void Every_key_Confluent_declares_is_classified()
    {
        HashSet<string> declared = [.. DeclaredKeys<ClientConfig>(), .. DeclaredKeys<ConsumerConfig>(), .. DeclaredKeys<AdminClientConfig>()];
        HashSet<string> catalogue = [.. KafkaSettingsCatalog.Secrets, .. KafkaSettingsCatalog.Paths, .. KafkaSettingsCatalog.Shown];

        declared.Should().NotBeEmpty("scanning nothing would pass for the wrong reason");
        declared.Except(catalogue).Should().BeEmpty("a key Confluent declares must be classified, or it would be withheld at this version");
        catalogue.Except(declared).Should().BeSubsetOf(
            ConfluentPropertyScopes.NewerThanTheFloor,
            "the only keys the catalogue holds that Confluent does not declare are the ones a newer version added");

        KafkaSettingsCatalog.Secrets.Intersect(KafkaSettingsCatalog.Paths).Should().BeEmpty();
        KafkaSettingsCatalog.Shown.Intersect(KafkaSettingsCatalog.Secrets).Should().BeEmpty();
        KafkaSettingsCatalog.Shown.Intersect(KafkaSettingsCatalog.Paths).Should().BeEmpty();
        ConfluentPropertyScopes.ProducerOnly.Intersect(catalogue).Should().BeEmpty("the engine's clones drop producer-only keys before anything is read");
    }

    [Fact]
    public void A_secret_shows_only_set_or_not_set()
    {
        string[] secrets =
        [
            "sasl.password", "ssl.key.pem", "sasl.oauthbearer.client.secret", "ssl.key.password", "ssl.keystore.password",
            "sasl.oauthbearer.config", "sasl.oauthbearer.extensions", "ssl.certificate.pem", "ssl.ca.pem",
        ];

        KafkaClusterSettingsView view = Read(config =>
        {
            foreach (string key in secrets)
            {
                config.Set(key, Sentinel + "-" + key);
            }

            config.Set("ssl.key.location", "/etc/kafka/key.pem");
            config.Set("custom.vendor.credential", Sentinel + "-credential");
            config.Set("sasl.username", "svc-account");
        });

        foreach (string key in secrets)
        {
            KafkaSettingRow row = Row(view, key);
            (row.Exposure, row.Shown).Should().Be((KafkaSettingExposure.Secret, "set"), key);
        }

        JsonSerializer.Serialize(view).Should().NotContain(Sentinel);
        view.Host.Select(static x => x.Shown).Should().NotContain(x => x.Contains(Sentinel, StringComparison.Ordinal));
        view.Overrides.Select(static x => x.KStudioValue + x.Why).Should().NotContain(x => x.Contains(Sentinel, StringComparison.Ordinal));
        Row(view, "sasl.username").Shown.Should().Be("svc-account", "a user name is not a secret");
        Row(view, "custom.vendor.credential").Exposure.Should().Be(KafkaSettingExposure.Secret);
    }

    [Fact]
    public void A_secret_that_is_empty_is_not_set()
    {
        KafkaClusterSettingsView view = Read(static config => config.Set("sasl.password", string.Empty));

        Row(view, "sasl.password").Shown.Should().Be("not set");
    }

    [Fact]
    public void An_unknown_key_is_withheld()
    {
        KafkaClusterSettingsView view = Read(static config => config.Set("some.future.setting", Sentinel));

        KafkaSettingRow row = Row(view, "some.future.setting");

        (row.Exposure, row.Shown).Should().Be((KafkaSettingExposure.Withheld, "value withheld: KStudio does not know this setting"));
        JsonSerializer.Serialize(view).Should().NotContain(Sentinel);
    }

    [Theory]
    [InlineData("my.custom.PASSWORD")]
    [InlineData("vendor.secret.value")]
    [InlineData("x.passphrase")]
    [InlineData("x.pem.bundle")]
    [InlineData("kerberos.keytab.file")]
    [InlineData("auth.token.thing")]
    [InlineData("a.credential.b")]
    [InlineData("tls.private.key.bytes")]
    public void A_key_named_for_a_secret_is_secret(string key)
    {
        KafkaClusterSettingsView view = Read(config => config.Set(key, Sentinel));

        Row(view, key).Exposure.Should().Be(KafkaSettingExposure.Secret);
        JsonSerializer.Serialize(view).Should().NotContain(Sentinel);
    }

    [Fact]
    public void A_path_is_shown_and_never_opened()
    {
        string missing = Path.Combine(Path.GetTempPath(), "ks-eng-c-does-not-exist", "client.key");
        string existing = Path.GetFullPath(typeof(KafkaSettingsReaderTests).Assembly.Location);

        KafkaClusterSettingsView view = Read(config =>
        {
            config.Set("ssl.key.location", missing);
            config.Set("ssl.ca.location", existing);
            config.Set("sasl.kerberos.keytab", "/etc/krb5.keytab");
        });

        Row(view, "ssl.key.location").Shown.Should().Be(missing, "a file that does not exist reads the same as one that does");
        Row(view, "ssl.ca.location").Shown.Should().Be(existing);
        Row(view, "sasl.kerberos.keytab").Exposure.Should().Be(KafkaSettingExposure.Path, "the lists decide before the name rule does");
        view.Host.Where(static x => x.Exposure == KafkaSettingExposure.Path).Should().HaveCount(3);
    }

    [Theory]
    [InlineData("https://user:pw@idp.example/oauth/token", "https://•••@idp.example/oauth/token")]
    [InlineData("https://u:p#ss@idp", "https://•••@idp")]
    [InlineData("https://idp.example/oauth", "https://idp.example/oauth")]
    [InlineData("broker-1:9092,broker-2:9092", "broker-1:9092,broker-2:9092")]
    public void User_info_in_a_url_is_masked(string value, string shown)
    {
        KafkaClusterSettingsView view = Read(config => config.Set("client.dns.lookup", value));

        Row(view, "client.dns.lookup").Shown.Should().Be(shown);
        JsonSerializer.Serialize(view).Should().NotContain("pw@").And.NotContain("ss@");
    }

    [Fact]
    public void A_key_the_engine_never_uses_is_removed()
    {
        KafkaClusterSettingsView view = Read(static config =>
        {
            config.Set("plugin.library.paths", "interceptor");
            config.Set("confluent.monitoring.interceptor.bootstrap.servers", "monitoring:9092");
            config.Set("group.instance.id", "static-1");
            config.Set("debug", "all");
            config.Set("fetch.message.max.bytes", "1");
            config.Set("auto.commit.enable", "true");
        });

        view.Host.Where(static x => x.Key is "plugin.library.paths" or "confluent.monitoring.interceptor.bootstrap.servers" or "group.instance.id" or "debug" or "fetch.message.max.bytes" or "auto.commit.enable")
            .Should().HaveCount(6).And.OnlyContain(static x => x.Exposure == KafkaSettingExposure.Removed && x.Shown == "removed: KStudio's clients never use it");
    }

    [Fact]
    public void The_overrides_match_ConfluentClientSettings()
    {
        (KafkaClusterDefinition definition, KafkaEngineSnapshot settings) = Define();
        KafkaClusterSettingsView view = new KafkaSettingsReader(settings).Read(definition);
        ConsumerConfig consumer = ConfluentClientSettings.BuildConsumerConfig(definition, Guid.Empty, WireIsolation.ReadCommitted, settings.Limits, settings.GroupIdPrefix);
        AdminClientConfig admin = ConfluentClientSettings.BuildAdminConfig(definition);

        Dictionary<string, string> overrides = view.Overrides.Where(static x => x.KStudioValue != "removed").ToDictionary(static x => x.Key, static x => x.KStudioValue);

        foreach (string key in new[]
        {
            "allow.auto.create.topics", "enable.auto.commit", "enable.auto.offset.store", "group.protocol", "enable.partition.eof", "auto.offset.reset",
            "statistics.interval.ms", "enable.metrics.push", "queued.max.messages.kbytes", "fetch.max.bytes", "max.partition.fetch.bytes", "client.id",
        })
        {
            overrides[key].Should().Be(consumer.Get(key), key);
        }

        overrides["group.id"].Should().Be("kstudio-{scan id}");
        overrides["isolation.level"].Should().StartWith("read_committed");
        overrides.Keys.Should().BeEquivalentTo(
            [.. consumer.Where(static x => !x.Key.StartsWith("bootstrap", StringComparison.Ordinal)).Select(static x => x.Key)],
            "the table lists exactly the keys the engine sets over the host's");
        admin.Get("allow.auto.create.topics").Should().Be("false");
        view.Overrides.Select(static x => x.Why).Should().OnlyContain(static x => x.Length > 0);
        view.Overrides.Single(static x => x.Key == "enable.metrics.push").Why.Should().Be("the studio's clients never appear in the cluster's client metrics");
        view.Overrides.Select(static x => x.Key).Should().Contain(["group.instance.id", "debug", "plugin.library.paths", "confluent.monitoring.interceptor.*", "fetch.message.max.bytes", "auto.commit.enable"]);
    }

    [Fact]
    public void A_host_value_the_engine_overrides_is_still_shown_as_the_hosts()
    {
        KafkaClusterSettingsView view = Read(static config => config.Set("enable.auto.commit", "true"));

        Row(view, "enable.auto.commit").Shown.Should().Be("true");
        view.Overrides.Single(static x => x.Key == "enable.auto.commit").KStudioValue.Should().Be("false");
    }

    [Fact]
    public void Separate_admin_config_marks_each_rows_role()
    {
        ConsumerConfig consumer = new() { BootstrapServers = "consumer:9092", GroupId = "host-group", SaslUsername = "same" };
        AdminClientConfig admin = new() { BootstrapServers = "admin:9092", SaslUsername = "same", SocketTimeoutMs = 1000 };
        KafkaEngineOptions options = new();
        options.AddCluster("prod", consumer, admin);
        KafkaClusterRegistry registry = new(options);

        KafkaClusterSettingsView view = new KafkaSettingsReader(registry.Settings).Read(registry.Get("prod"));

        view.Host.Where(static x => x.Key == "bootstrap.servers").Select(static x => (x.Role, x.Shown)).Should().Equal(
            (KafkaSettingRole.ScansOnly, "consumer:9092"),
            (KafkaSettingRole.MetadataOnly, "admin:9092"));
        Row(view, "sasl.username").Role.Should().Be(KafkaSettingRole.Both);
        Row(view, "group.id").Role.Should().Be(KafkaSettingRole.ScansOnly);
        Row(view, "socket.timeout.ms").Role.Should().Be(KafkaSettingRole.MetadataOnly);
        view.Host.Select(static x => x.Key).Should().BeInAscendingOrder(StringComparer.Ordinal);
    }

    [Fact]
    public void A_token_refresh_hook_is_a_flag_and_never_the_delegate()
    {
        ClientConfig config = new() { BootstrapServers = "broker:9092" };
        KafkaEngineOptions options = new();
        options.AddCluster("with", config, static c => c.OAuthBearerTokenRefresh(static (_, _) => { }));
        options.AddCluster("without", config);
        KafkaClusterRegistry registry = new(options);
        KafkaSettingsReader reader = new(registry.Settings);

        reader.Read(registry.Get("with")).TokenRefreshDeclared.Should().BeTrue();
        reader.Read(registry.Get("without")).TokenRefreshDeclared.Should().BeFalse();
    }
}
