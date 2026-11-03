using System.ComponentModel;
using System.Reflection;

using Npgsql;

using PostgresStudio.Services.Connection;

namespace PostgresStudio.Tests.Connection;

/// <summary>A stated process environment.</summary>
internal sealed class FakeEnvironment(params (string Name, string Value)[] variables) : IProcessEnvironment
{
    private readonly Dictionary<string, string> variables = variables.ToDictionary(static x => x.Name, static x => x.Value, StringComparer.Ordinal);

    /// <summary>Every name the reader asked for, for the test that holds it to the catalogue.</summary>
    public List<string> Asked { get; } = [];

    /// <inheritdoc />
    public string? Get(string name)
    {
        Asked.Add(name);
        return variables.GetValueOrDefault(name);
    }
}

/// <summary>NP1: the reader's provenance, redaction and parsing rules (NP-DESIGN §2, §3), against stated connection strings and environments.</summary>
public sealed class NpgsqlSettingsReaderTests
{
    private static ConnectionSettings Read(
        string connectionString,
        FakeEnvironment? environment = null,
        int major = 10,
        IReadOnlySet<string>? serverNames = null) =>
        NpgsqlSettingsReader.Read(connectionString, new ConnectionReadContext
        {
            Environment = environment ?? new FakeEnvironment(),
            NpgsqlMajor = major,
            ServerSettingNames = serverNames,
        });

    private static ConnectionSettingRow Row(ConnectionSettings settings, string keyword) =>
        settings.Rows.Single(x => string.Equals(x.Keyword, keyword, StringComparison.Ordinal));

    // -------------------------------------------------------------------------------------------------
    // Provenance: the connection string over the environment over the default
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public void The_connection_string_wins_over_the_environment_which_wins_over_the_default()
    {
        FakeEnvironment env = new(("PGAPPNAME", "from-env"), ("PGTZ", "Europe/Helsinki"));

        ConnectionSettings settings = Read("Host=db.example;Application Name=from-string", env);

        ConnectionSettingRow applicationName = Row(settings, "Application Name");
        applicationName.Origin.Should().Be(ConnectionSettingOrigin.ConnectionString);
        applicationName.Value.Should().Be("from-string");
        applicationName.IsExplicit.Should().BeTrue();

        ConnectionSettingRow timezone = Row(settings, "Timezone");
        timezone.Origin.Should().Be(ConnectionSettingOrigin.Environment);
        timezone.Value.Should().Be("Europe/Helsinki");
        timezone.EnvironmentVariable.Should().Be("PGTZ");

        ConnectionSettingRow maximum = Row(settings, "Maximum Pool Size");
        maximum.Origin.Should().Be(ConnectionSettingOrigin.Default);
        maximum.Value.Should().Be("100");
        maximum.IsExplicit.Should().BeFalse();
    }

    [Fact]
    public void A_keyword_set_to_its_own_default_is_still_explicit()
    {
        // NpgsqlConnectionStringBuilder.ContainsKey answers for every keyword Npgsql knows, so it cannot tell this from the default.
        ConnectionSettings settings = Read("Host=db.example;Pooling=true;Maximum Pool Size=100");

        Row(settings, "Pooling").Origin.Should().Be(ConnectionSettingOrigin.ConnectionString);
        Row(settings, "Maximum Pool Size").Origin.Should().Be(ConnectionSettingOrigin.ConnectionString);
        Row(settings, "Minimum Pool Size").Origin.Should().Be(ConnectionSettingOrigin.Default);
    }

    [Fact]
    public void An_alias_sets_the_keyword_it_is_an_alias_of()
    {
        ConnectionSettings settings = Read("Server=db.example;User Id=orders;Load Balance Timeout=0");

        Row(settings, "Host").Value.Should().Be("db.example");
        Row(settings, "Host").Origin.Should().Be(ConnectionSettingOrigin.ConnectionString);
        Row(settings, "Username").Value.Should().Be("orders");
        Row(settings, "Connection Lifetime").Value.Should().Be("0");
        Row(settings, "Connection Lifetime").Origin.Should().Be(ConnectionSettingOrigin.ConnectionString);
    }

    [Fact]
    public void PGAPPNAME_is_honoured_only_by_Npgsql_10()
    {
        FakeEnvironment env = new(("PGAPPNAME", "from-env"));

        ConnectionSettingRow ten = Row(Read("Host=db.example", env, major: 10), "Application Name");
        ten.Origin.Should().Be(ConnectionSettingOrigin.Environment);
        ten.Value.Should().Be("from-env");

        ConnectionSettingRow nine = Row(Read("Host=db.example", env, major: 9), "Application Name");
        nine.Origin.Should().Be(ConnectionSettingOrigin.Default);
        nine.Value.Should().Be("not set");
        nine.EnvironmentVariable.Should().BeNull();
    }

    [Fact]
    public void PGUSER_stands_in_for_an_unset_or_empty_Username_and_not_for_a_set_one()
    {
        FakeEnvironment env = new(("PGUSER", "from-env"));

        Row(Read("Host=db.example", env), "Username").Value.Should().Be("from-env");
        Row(Read("Host=db.example;Username=", env), "Username").Origin.Should().Be(ConnectionSettingOrigin.Environment);
        Row(Read("Host=db.example;Username=orders", env), "Username").Value.Should().Be("orders");
    }

    [Fact]
    public void PGTARGETSESSIONATTRS_is_read_only_for_a_data_source_with_more_than_one_host()
    {
        FakeEnvironment env = new(("PGTARGETSESSIONATTRS", "primary"));

        ConnectionSettingRow single = Row(Read("Host=db.example", env), "Target Session Attributes");
        single.Origin.Should().NotBe(ConnectionSettingOrigin.Environment);
        single.Note.Should().Contain("more than one host");

        ConnectionSettingRow multi = Row(Read("Host=a.example,b.example", env), "Target Session Attributes");
        multi.Origin.Should().Be(ConnectionSettingOrigin.Environment);
        multi.Value.Should().Be("primary");
    }

    [Fact]
    public void A_newer_Npgsql_than_the_studio_knows_uses_the_newest_table_and_says_so()
    {
        ConnectionSettings settings = Read("Host=db.example", new FakeEnvironment(("PGAPPNAME", "from-env")), major: 11);

        settings.NewerThanStudioKnows.Should().BeTrue();
        settings.Notes.Should().ContainSingle().Which.Should().Contain("newer than the studio knows");
        Row(settings, "Application Name").Origin.Should().Be(ConnectionSettingOrigin.Environment);
        Read("Host=db.example", major: 10).NewerThanStudioKnows.Should().BeFalse();
    }

    [Fact]
    public void The_reader_asks_the_environment_only_for_the_variables_the_catalogue_lists()
    {
        FakeEnvironment env = new();

        Read("Host=db.example", env);

        env.Asked.Should().OnlyContain(x => NpgsqlSettingsCatalog.EnvironmentVariables.Contains(x));
    }

    // -------------------------------------------------------------------------------------------------
    // The loaded builder
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public void Every_keyword_of_the_loaded_builder_has_a_row_and_none_is_unreadable()
    {
        // A fresh builder's getters: none throws, at either major (this suite runs at the floor and at the latest tested).
        string[] keywords = [.. typeof(NpgsqlConnectionStringBuilder)
            .GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(static x => x.GetCustomAttribute<DisplayNameAttribute>()?.DisplayName)
            .OfType<string>()];

        ConnectionSettings settings = Read("Host=db.example", major: NpgsqlSettingsReader.LoadedMajor);

        settings.Rows.Select(static x => x.Keyword).Should().BeEquivalentTo(keywords);
        settings.Rows.Should().NotContain(static x => x.Origin == ConnectionSettingOrigin.Unreadable);
        settings.NpgsqlMajor.Should().Be(NpgsqlSettingsReader.LoadedMajor);
        settings.NpgsqlVersion.Should().StartWith(NpgsqlSettingsReader.LoadedMajor + ".");
    }

    [Fact]
    public void The_rows_come_in_the_catalogues_order_and_carry_Npgsqls_category()
    {
        ConnectionSettings settings = Read("Host=db.example");

        settings.Rows[0].Keyword.Should().Be("Host");
        Row(settings, "Host").Category.Should().Be("Connection");
        Row(settings, "Maximum Pool Size").Category.Should().Be("Pooling");
        Row(settings, "SSL Mode").Category.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void An_obsolete_keyword_carries_Npgsqls_message()
    {
        ConnectionSettings settings = Read("Host=db.example;Trust Server Certificate=true");

        ConnectionSettingRow row = Row(settings, "Trust Server Certificate");
        row.Obsolete.Should().Contain("does nothing");
        row.IsExplicit.Should().BeTrue();
        Row(settings, "Pooling").Obsolete.Should().BeNull();
    }

    [Fact]
    public void A_connection_string_Npgsql_will_not_parse_is_unreadable_and_its_message_is_not_kept()
    {
        ConnectionSettings settings = Read("Host=db.example;Password=S3NT1NEL-pw;Port=S3NT1NEL-port");

        settings.Readable.Should().BeFalse();
        settings.Rows.Should().BeEmpty();
        settings.UnreadableReason.Should().Be(nameof(ArgumentException));
        settings.UnreadableReason.Should().NotContain("S3NT1NEL");
        settings.Hints.Should().BeEmpty();
    }

    [Fact]
    public void No_connection_string_is_unreadable_too()
    {
        NpgsqlSettingsReader.Read((string?) null, new ConnectionReadContext()).Readable.Should().BeFalse();
    }

    [Fact]
    public void The_hosts_are_parsed_and_the_effective_values_are_typed()
    {
        ConnectionSettings settings = Read("Host=a.example,b.example:6432;Port=5433;Maximum Pool Size=20;Pooling=false");

        settings.Endpoints.Should().Equal(("a.example", 5433), ("b.example", 6432));
        settings.IsMultiHost.Should().BeTrue();
        settings.Values.MaxPoolSize.Should().Be(20);
        settings.Values.Pooling.Should().BeFalse();
        settings.Values.SslMode.Should().Be("Prefer", "Npgsql's default");
        settings.Values.CertificateNotVerified.Should().BeTrue();
        settings.PoolerSigns.Should().ContainSingle().Which.Kind.Should().Be(PoolerSignKind.PgBouncerPort);
    }

    // -------------------------------------------------------------------------------------------------
    // Secrets
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public void The_password_is_not_visible_unless_Persist_Security_Info_keeps_it_and_the_studio_claims_neither()
    {
        ConnectionSettingRow hidden = Row(Read("Host=db.example"), "Password");

        hidden.Origin.Should().Be(ConnectionSettingOrigin.NotVisible);
        hidden.Value.Should().Be("not visible: Npgsql removes it from the data source's connection string");
        hidden.Exposure.Should().Be(NpgsqlSettingExposure.Secret);
    }

    [Fact]
    public void With_Persist_Security_Info_a_password_is_set_or_not_set_and_never_shown()
    {
        ConnectionSettings set = Read("Host=db.example;Persist Security Info=true;Password=S3NT1NEL-Password");
        Row(set, "Password").Value.Should().Be("set");
        Row(set, "Password").Origin.Should().Be(ConnectionSettingOrigin.ConnectionString);
        set.PersistSecurityInfo.Should().BeTrue();

        ConnectionSettings unset = Read("Host=db.example;Persist Security Info=true");
        Row(unset, "Password").Value.Should().Be("not set");
        Row(unset, "Password").Note.Should().Contain("password file").And.Contain("never looks for it");
    }

    [Fact]
    public void PGPASSWORD_is_set_and_never_read_out()
    {
        FakeEnvironment env = new(("PGPASSWORD", "S3NT1NEL-env-pgpassword"));

        ConnectionSettingRow visible = Row(Read("Host=db.example;Persist Security Info=true", env), "Password");
        visible.Value.Should().Be("set (PGPASSWORD)");
        visible.Origin.Should().Be(ConnectionSettingOrigin.Environment);

        // With the password removed from the data source's string, the studio cannot say which one Npgsql will use.
        ConnectionSettings hidden = Read("Host=db.example", env);
        Row(hidden, "Password").Origin.Should().Be(ConnectionSettingOrigin.NotVisible);
        Row(hidden, "Password").Note.Should().Contain("PGPASSWORD is set").And.NotContain("S3NT1NEL");
        hidden.PasswordEnvironmentSet.Should().BeTrue();
    }

    [Fact]
    public void The_SSL_password_is_set_or_not_set()
    {
        ConnectionSettings set = Read("Host=db.example;SSL Password=S3NT1NEL-sslpw");
        Row(set, "SSL Password").Value.Should().Be("set");
        set.SslPasswordSet.Should().BeTrue();

        ConnectionSettings unset = Read("Host=db.example");
        Row(unset, "SSL Password").Value.Should().Be("not set");
        unset.SslPasswordSet.Should().BeFalse();
    }

    [Fact]
    public void A_path_is_shown_and_its_default_file_is_never_probed()
    {
        ConnectionSettings settings = Read(
            "Host=db.example;Passfile=/etc/app/pgpass;SSL Key=/etc/app/client.key",
            new FakeEnvironment(("PGSSLROOTCERT", "/etc/ssl/root.crt")));

        Row(settings, "Passfile").Value.Should().Be("/etc/app/pgpass");
        Row(settings, "SSL Key").Value.Should().Be("/etc/app/client.key");

        ConnectionSettingRow root = Row(settings, "Root Certificate");
        root.Value.Should().Be("/etc/ssl/root.crt");
        root.Origin.Should().Be(ConnectionSettingOrigin.Environment);
        root.EnvironmentVariable.Should().Be("PGSSLROOTCERT");

        Row(settings, "SSL Certificate").Note.Should().Contain("never looks for it");
    }

    // -------------------------------------------------------------------------------------------------
    // Options
    // -------------------------------------------------------------------------------------------------

    private static HashSet<string> Server(params string[] names) => new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Options_names_are_always_shown_and_a_value_only_for_a_server_setting_without_a_dot()
    {
        ConnectionOptions options = NpgsqlSettingsReader.ParseOptions(
            "-c statement_timeout=5s -c app.secret=S3NT1NEL-opt --work-mem=64MB -cdatestyle=ISO",
            Server("statement_timeout", "work_mem", "datestyle"));

        options.Items.Select(static x => x.Name).Should().Equal("statement_timeout", "app.secret", "work_mem", "datestyle");
        options.Items[0].Value.Should().Be("5s");
        options.Items[1].Value.Should().BeNull();
        options.Items[1].WithheldBecause.Should().Contain("custom");
        options.Items[2].Value.Should().Be("64MB");
        options.Items[3].Value.Should().Be("ISO");
        options.OtherWithheld.Should().BeFalse();
        options.Render().Should().NotContain("S3NT1NEL");
    }

    [Fact]
    public void An_unknown_name_and_an_unchecked_one_have_their_values_withheld()
    {
        NpgsqlSettingsReader.ParseOptions("-c nonsense=S3NT1NEL-x", Server("statement_timeout")).Items.Single().Value.Should().BeNull();
        NpgsqlSettingsReader.ParseOptions("-c statement_timeout=5s", serverNames: null).Items.Single().Value.Should().BeNull();
    }

    [Fact]
    public void Text_that_is_not_a_setting_is_withheld_as_a_whole()
    {
        ConnectionOptions options = NpgsqlSettingsReader.ParseOptions("-c statement_timeout=5s -d S3NT1NEL-database -c noequals -c =x", Server("statement_timeout"));

        options.Items.Should().ContainSingle();
        options.OtherWithheld.Should().BeTrue();
        options.Render().Should().EndWith("(other options withheld)").And.NotContain("S3NT1NEL");
    }

    [Fact]
    public void A_backslash_keeps_a_space_in_a_value()
    {
        NpgsqlSettingsReader.ParseOptions(@"-c search_path=a\ b", Server("search_path")).Items.Single().Value.Should().Be("a b");
    }

    [Fact]
    public void The_Options_row_renders_the_parsed_options_and_PGOPTIONS_stands_in_for_unset_ones()
    {
        ConnectionSettings fromString = Read("Host=db.example;Options=-c statement_timeout=5s", serverNames: Server("statement_timeout"));
        Row(fromString, "Options").Value.Should().Be("-c statement_timeout=5s");

        ConnectionSettings fromEnvironment = Read("Host=db.example", new FakeEnvironment(("PGOPTIONS", "-c work_mem=64MB")), serverNames: Server("work_mem"));
        ConnectionSettingRow row = Row(fromEnvironment, "Options");
        row.Origin.Should().Be(ConnectionSettingOrigin.Environment);
        row.Value.Should().Be("-c work_mem=64MB");

        Row(Read("Host=db.example"), "Options").Value.Should().Be("not set");
    }

    [Fact]
    public void The_names_asked_of_the_server_are_lower_case_server_settings_only()
    {
        NpgsqlSettingsReader.OptionNames("Host=h;Options=-c Statement_Timeout=5s -c app.secret=x --work-mem=1MB", new FakeEnvironment())
            .Should().Equal("statement_timeout", "work_mem");
        NpgsqlSettingsReader.OptionNames("Host=h", new FakeEnvironment(("PGOPTIONS", "-c work_mem=1MB"))).Should().Equal("work_mem");
        NpgsqlSettingsReader.OptionNames("Host=h;Port=nope", new FakeEnvironment()).Should().BeEmpty();
        NpgsqlSettingsReader.OptionNames(null, new FakeEnvironment()).Should().BeEmpty();
    }
}
