using System.ComponentModel;
using System.Reflection;
using System.Text.Json;

using Npgsql;

using PostgresStudio.Services.Connection;

namespace PostgresStudio.Tests.Connection;

/// <summary>
/// NP1's sentinel test (NP-DESIGN §2.6) for the model: a connection string whose every string keyword holds
/// <c>S3NT1NEL-&lt;keyword&gt;</c>, both passwords, a custom option and <c>PGPASSWORD</c> - and no withheld sentinel anywhere in
/// the model, however it is dumped. NP3, NP4 and PASTE repeat it for the surfaces they add.
/// </summary>
public sealed class ConnectionSentinelTests
{
    private static readonly JsonSerializerOptions Json = new() { IncludeFields = true, MaxDepth = 64 };

    private static string Sentinel(string keyword) => "S3NT1NEL-" + keyword;

    /// <summary>The sentinels that must never appear: the secrets, the custom option's value, and the environment's password.</summary>
    private static readonly string[] Withheld =
    [
        Sentinel("Password"), Sentinel("SSL Password"), Sentinel("opt"), "S3NT1NEL-env-pgpassword",
    ];

    private static string StringKeywordsConnectionString()
    {
        NpgsqlConnectionStringBuilder builder = new() { PersistSecurityInfo = true };

        foreach (PropertyInfo property in typeof(NpgsqlConnectionStringBuilder).GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            if (property.PropertyType == typeof(string)
                && property.GetCustomAttribute<DisplayNameAttribute>()?.DisplayName is { } keyword
                && property.GetCustomAttribute<ObsoleteAttribute>() is null
                && keyword is not ("Options" or "Host" or "Target Session Attributes" or "Require Auth"))
            {
                builder[keyword] = Sentinel(keyword);
            }
        }

        builder.Host = "db.example";
        builder.Options = "-c app.secret=" + Sentinel("opt");
        return builder.ConnectionString;
    }

    private static ConnectionSettings Read(out string dump, IReadOnlySet<string>? serverNames = null)
    {
        ConnectionSettings settings = NpgsqlSettingsReader.Read(
            StringKeywordsConnectionString(),
            new ConnectionReadContext
            {
                Environment = new FakeEnvironment(("PGPASSWORD", "S3NT1NEL-env-pgpassword"), ("PGSSLKEY", "S3NT1NEL-env-sslkey")),
                ServerSettingNames = serverNames,
            });

        dump = JsonSerializer.Serialize(settings, Json)
            + string.Join('\n', settings.Rows.Select(static x => x.Keyword + "=" + x.Value + " " + x.Note + " " + x.EnvironmentVariable))
            + string.Join('\n', settings.Values.All.Select(static x => x.Key + "=" + x.Value))
            + string.Join('\n', settings.Hints.Select(static x => x.Text + x.ConnectionStringFix + x.BuilderFix))
            + settings.Options?.Render()
            + string.Join('\n', settings.Notes)
            + settings;

        return settings;
    }

    [Fact]
    public void No_withheld_sentinel_is_in_the_model_however_it_is_dumped()
    {
        ConnectionSettings settings = Read(out string dump, serverNames: new HashSet<string>(["statement_timeout"]));

        settings.Readable.Should().BeTrue(settings.UnreadableReason);
        foreach (string sentinel in Withheld)
        {
            dump.Should().NotContain(sentinel);
        }
    }

    [Fact]
    public void The_dump_is_not_vacuous_a_shown_keywords_sentinel_is_in_it_and_the_secrets_are_reported_as_set()
    {
        ConnectionSettings settings = Read(out string dump);

        dump.Should().Contain(Sentinel("Application Name")).And.Contain(Sentinel("Database"));
        settings.Rows.Single(static x => x.Keyword == "Password").Value.Should().Be("set");
        settings.Rows.Single(static x => x.Keyword == "SSL Password").Value.Should().Be("set");
        settings.Rows.Single(static x => x.Keyword == "Options").Value.Should().Contain("app.secret=(value withheld)");

        // Paths are shown; and the connection string's own SSL Key wins over PGSSLKEY.
        settings.Rows.Single(static x => x.Keyword == "SSL Key").Value.Should().Be(Sentinel("SSL Key"));
        dump.Should().NotContain("S3NT1NEL-env-sslkey");
    }

    [Fact]
    public void The_environments_password_and_key_do_not_leak_when_the_connection_string_names_neither()
    {
        ConnectionSettings settings = NpgsqlSettingsReader.Read(
            "Host=db.example;Persist Security Info=true",
            new ConnectionReadContext { Environment = new FakeEnvironment(("PGPASSWORD", "S3NT1NEL-env-pgpassword"), ("PGSSLKEY", "S3NT1NEL-env-sslkey")) });

        string dump = JsonSerializer.Serialize(settings, Json)
            + string.Join('\n', settings.Rows.Select(static x => x.Value + x.Note));

        dump.Should().NotContain("S3NT1NEL-env-pgpassword");
        settings.Rows.Single(static x => x.Keyword == "Password").Value.Should().Be("set (PGPASSWORD)");
        settings.Rows.Single(static x => x.Keyword == "SSL Key").Value.Should().Be("S3NT1NEL-env-sslkey", "a path is shown on the terms of its keyword");
    }
}
