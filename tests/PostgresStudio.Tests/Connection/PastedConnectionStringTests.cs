using System.Net;
using System.Net.Sockets;
using System.Text.Json;

using PostgresStudio.Services.Connection;

namespace PostgresStudio.Tests.Connection;

/// <summary>
/// 1.1-PASTE: what happens to text a visitor pastes before the reader sees it. Secrets come out through Npgsql's own keyword
/// canonicalisation, a rejection says a name and never a value, text Npgsql cannot read says nothing, and none of it connects to
/// anything. Every sentinel is a value that must appear in no model, however it is dumped.
/// </summary>
public sealed class PastedConnectionStringTests
{
    private const string Sentinel = "S3NT1NEL";

    private static readonly JsonSerializerOptions Dump = new() { IncludeFields = true, MaxDepth = 64 };

    private static PastedConnection Read(string? text, FakeEnvironment? environment = null) =>
        PastedConnectionString.Read(text, new ConnectionReadContext { Environment = environment ?? new FakeEnvironment() });

    private static string Everything(PastedConnection read) =>
        JsonSerializer.Serialize(read, Dump)
        + string.Join('\n', read.Settings?.Rows.Select(static x => x.Keyword + "=" + x.Value + " " + x.Note + " " + x.DefaultValue) ?? [])
        + string.Join('\n', read.Settings?.Hints.Select(static x => x.Text + x.ConnectionStringFix + x.BuilderFix) ?? [])
        + string.Join('\n', read.Rejected.Select(static x => ConnectionCompareView.Sentence(x)));

    private static ConnectionSettingRow Row(PastedConnection read, string keyword) =>
        read.Settings!.Rows.Single(x => string.Equals(x.Keyword, keyword, StringComparison.Ordinal));

    // -------------------------------------------------------------------------------------------------
    // Secrets come out first
    // -------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Password")]
    [InlineData("password")]
    [InlineData("PWD")]
    [InlineData("Pwd")]
    [InlineData("PSW")]
    [InlineData("SSL Password")]
    [InlineData("ssl password")]
    [InlineData("SslPassword")]
    public void A_password_and_every_alias_of_it_is_removed_whatever_its_spelling(string keyword)
    {
        PastedConnection read = Read("Host=db.example;" + keyword + "=" + Sentinel + ";Database=app");

        read.Outcome.Should().Be(PastedConnectionOutcome.Read);
        read.RemovedSecrets.Should().ContainSingle("the keyword is resolved through Npgsql, so an alias is the secret it stands for");
        Everything(read).Should().NotContain(Sentinel);
        read.Settings!.Values.Text("Database").Should().Be("app", "the rest of the text is read");
    }

    [Fact]
    public void A_removed_password_is_reported_as_set_and_nothing_more()
    {
        PastedConnection read = Read("Host=db.example;Pwd=" + Sentinel + ";SSL Password=" + Sentinel + ";Database=app");

        read.RemovedSecrets.Should().Equal("Password", "SSL Password");

        ConnectionSettingRow password = Row(read, "Password");
        password.Value.Should().Be("set");
        password.Origin.Should().Be(ConnectionSettingOrigin.Pasted);
        password.Note.Should().Contain("removed before anything was read");
        Row(read, "SSL Password").Value.Should().Be("set");
        read.Settings!.SslPasswordSet.Should().BeTrue("the SSL password was in the text, so the settings say one is set");
    }

    [Fact]
    public void A_text_with_no_password_says_not_set_and_not_the_data_sources_not_visible()
    {
        PastedConnection read = Read("Host=db.example;Database=app");

        Row(read, "Password").Value.Should().Be("not set");
        Row(read, "Password").Origin.Should().Be(ConnectionSettingOrigin.Default);
        read.RemovedSecrets.Should().BeEmpty();
    }

    [Fact]
    public void A_quoted_password_with_separators_in_it_is_removed_whole()
    {
        PastedConnection read = Read("Host=db.example;Password='a;b=" + Sentinel + "';Database=app;SSL Password=\"x\"\"y;" + Sentinel + "\";Application Name=svc");

        read.Outcome.Should().Be(PastedConnectionOutcome.Read);
        Everything(read).Should().NotContain(Sentinel).And.NotContain("b=").And.NotContain("y;");
        read.Settings!.Values.Text("Database").Should().Be("app");
        read.Settings.Values.Text("Application Name").Should().Be("svc");
    }

    [Fact]
    public void Both_spellings_of_one_password_are_removed_and_the_later_never_wins()
    {
        PastedConnection read = Read("Host=db.example;Password=" + Sentinel + "-a;PWD=" + Sentinel + "-b");

        Everything(read).Should().NotContain(Sentinel);
        read.RemovedSecrets.Should().ContainSingle().Which.Should().Be("Password");
    }

    [Fact]
    public void The_environments_password_is_still_reported_as_set_by_its_variable_only()
    {
        PastedConnection read = Read("Host=db.example", new FakeEnvironment(("PGPASSWORD", Sentinel + "-env")));

        Row(read, "Password").Value.Should().Be("set (PGPASSWORD)");
        Everything(read).Should().NotContain(Sentinel);
    }

    // -------------------------------------------------------------------------------------------------
    // Options
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public void An_options_value_is_never_read_and_its_name_is_shown()
    {
        PastedConnection read = Read("Host=db.example;Options='-c app.secret=" + Sentinel + " -c search_path=" + Sentinel + " --statement_timeout=" + Sentinel + " stray'");

        ConnectionSettingRow options = Row(read, "Options");
        options.Value.Should().Contain("-c app.secret=(value withheld)").And.Contain("-c search_path=(value withheld)").And.Contain("-c statement_timeout=(value withheld)").And.Contain("(other options withheld)");
        options.Origin.Should().Be(ConnectionSettingOrigin.Pasted);
        Everything(read).Should().NotContain(Sentinel).And.NotContain("stray");
    }

    [Fact]
    public void An_options_value_is_withheld_even_for_a_name_the_server_has()
    {
        PastedConnection read = PastedConnectionString.Read(
            "Host=db.example;Options=-c search_path=" + Sentinel,
            new ConnectionReadContext { Environment = new FakeEnvironment(), ServerSettingNames = new HashSet<string>(["search_path"], StringComparer.OrdinalIgnoreCase) });

        Row(read, "Options").Value.Should().Be("-c search_path=(value withheld)", "a pasted string is never looked up: a name, never a value");
        Everything(read).Should().NotContain(Sentinel);
    }

    // -------------------------------------------------------------------------------------------------
    // Refusals say a name, and nothing else
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public void A_keyword_npgsql_does_not_know_is_refused_by_name_and_its_value_is_never_said()
    {
        PastedConnection read = Read("Host=db.example;Password='" + Sentinel + "';Bogus Keyword=" + Sentinel + ";Database=app");

        read.Outcome.Should().Be(PastedConnectionOutcome.Rejected);
        read.Settings.Should().BeNull("a refused string is not read at all");
        read.Rejected.Should().ContainSingle().Which.Should().Be(new PastedRejection("Bogus Keyword", Known: false));
        ConnectionCompareView.Sentence(read.Rejected[0]).Should().Be("Npgsql does not know the keyword \"Bogus Keyword\".");
        Everything(read).Should().NotContain(Sentinel);
    }

    [Fact]
    public void A_value_npgsql_will_not_accept_is_refused_by_the_keywords_name_alone()
    {
        PastedConnection read = Read("Host=db.example;Port=" + Sentinel + ";Maximum Pool Size=" + Sentinel);

        read.Outcome.Should().Be(PastedConnectionOutcome.Rejected);
        read.Rejected.Select(static x => x.Keyword).Should().Equal("Port", "Maximum Pool Size");
        read.Rejected.Should().OnlyContain(static x => x.Known);
        ConnectionCompareView.Sentence(read.Rejected[0]).Should().Be("Npgsql did not accept the value given for \"Port\".");
        Everything(read).Should().NotContain(Sentinel);
    }

    [Fact]
    public void A_keyword_after_an_unquoted_password_is_refused_without_its_name_because_it_may_be_the_rest_of_the_password()
    {
        // Npgsql ends an unquoted password at the first ';': "ghi" is the password's tail, and "def..." the start of it.
        PastedConnection read = Read("Host=x;Password=abc;def" + Sentinel + "=ghi");

        read.Outcome.Should().Be(PastedConnectionOutcome.Rejected);
        read.Rejected.Should().ContainSingle().Which.Should().Be(new PastedRejection(string.Empty, Known: false, AfterPassword: true));
        ConnectionCompareView.Sentence(read.Rejected[0]).Should().Be("A keyword after a password could not be read: a password with ';' in it must be quoted.");
        Everything(read).Should().NotContain(Sentinel).And.NotContain("abc").And.NotContain("ghi");
    }

    [Fact]
    public void Every_segment_refused_after_an_unquoted_password_is_one_unnamed_refusal_and_the_alias_counts()
    {
        PastedConnection read = Read("Host=x;Pwd=abc;one" + Sentinel + "=1;two" + Sentinel + "=2");

        read.Outcome.Should().Be(PastedConnectionOutcome.Rejected);
        read.Rejected.Should().ContainSingle().Which.AfterPassword.Should().BeTrue();
        Everything(read).Should().NotContain(Sentinel);
    }

    [Fact]
    public void A_value_refused_after_an_unquoted_ssl_password_is_not_named_either()
    {
        PastedConnection read = Read("Host=x;SSL Password=abc;Port=" + Sentinel);

        read.Rejected.Should().ContainSingle().Which.Should().Be(new PastedRejection(string.Empty, Known: false, AfterPassword: true));
        Everything(read).Should().NotContain(Sentinel);
    }

    [Fact]
    public void A_keyword_after_a_quoted_password_is_still_refused_by_name_because_the_password_was_whole()
    {
        PastedConnection read = Read("Host=x;Password='abc;def';Bogus=" + Sentinel);

        read.Rejected.Should().ContainSingle().Which.Should().Be(new PastedRejection("Bogus", Known: false));
        Everything(read).Should().NotContain(Sentinel);
    }

    [Fact]
    public void A_keyword_after_a_setting_that_followed_an_unquoted_password_is_named_again()
    {
        PastedConnection read = Read("Host=x;Password=abc;Port=5432;Bogus=" + Sentinel);

        read.Rejected.Should().ContainSingle().Which.Should().Be(new PastedRejection("Bogus", Known: false), "Port was read, so the password ended before it");
    }

    [Theory]
    [InlineData("Host=x;Password=;Database=y")]
    [InlineData("Host=x;Options=;Database=y")]
    [InlineData("Host=x;SSL Password=;Database=y")]
    [InlineData("Host=x;Pwd=;Database=y")]
    [InlineData("Host=x;Password='';Database=y")]
    public void An_empty_value_sets_nothing_and_the_rest_of_the_string_is_read(string text)
    {
        PastedConnection read = Read(text);

        read.Outcome.Should().Be(PastedConnectionOutcome.Read, "Npgsql reads a string with an empty value");
        read.Settings!.Values.Text("Database").Should().Be("y");
        read.Settings.Values.Text("Host").Should().Be("x");
    }

    [Fact]
    public void An_empty_password_is_not_a_removed_secret_and_does_not_make_the_next_refusal_unnamed()
    {
        PastedConnection read = Read("Host=x;Password=;Bogus=" + Sentinel);

        read.Rejected.Should().ContainSingle().Which.Should().Be(new PastedRejection("Bogus", Known: false));
    }

    [Theory]
    [InlineData("postgresql://user:" + Sentinel + "@db.example/app?sslmode=require")]
    [InlineData(Sentinel)]
    [InlineData("Host=db.example;" + Sentinel)]
    [InlineData("Host=db.example;Password='" + Sentinel)]
    [InlineData("Host=db.example;Password='" + Sentinel + "' trailing")]
    [InlineData("=" + Sentinel)]
    [InlineData("\"Host\"=" + Sentinel)]
    [InlineData(";;;")]
    public void Text_that_is_not_a_connection_string_is_unreadable_and_says_nothing_of_itself(string text)
    {
        PastedConnection read = Read(text);

        read.Outcome.Should().BeOneOf(PastedConnectionOutcome.Unreadable, PastedConnectionOutcome.Empty);
        read.Rejected.Should().BeEmpty("a name that is not a keyword is not echoed: a URL's user and password are in it");
        Everything(read).Should().NotContain(Sentinel);
    }

    [Fact]
    public void Nothing_and_too_much_are_their_own_outcomes()
    {
        Read(null).Outcome.Should().Be(PastedConnectionOutcome.Empty);
        Read("  \n ").Outcome.Should().Be(PastedConnectionOutcome.Empty);
        Read(new string('a', PastedConnectionString.MaxCharacters + 1)).Outcome.Should().Be(PastedConnectionOutcome.TooLong);
        Read("Host=" + new string('a', PastedConnectionString.MaxCharacters - 5)).Outcome.Should().Be(PastedConnectionOutcome.Read, "the limit is a limit on the text, and the text at it is read");
    }

    [Fact]
    public void A_text_with_more_keywords_than_npgsql_has_is_unreadable()
    {
        Read(string.Join(';', Enumerable.Range(0, 200).Select(static i => "Application Name=n" + i))).Outcome.Should().Be(PastedConnectionOutcome.Unreadable);
    }

    // -------------------------------------------------------------------------------------------------
    // What is read
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public void What_the_text_sets_says_pasted_and_what_it_does_not_says_default()
    {
        PastedConnection read = Read("Host=db.example;Maximum Pool Size=50;Application Name=svc");

        ConnectionSettingRow pool = Row(read, "Maximum Pool Size");
        pool.Origin.Should().Be(ConnectionSettingOrigin.Pasted);
        pool.IsExplicit.Should().BeTrue();
        pool.Value.Should().Be("50");

        ConnectionSettingRow timeout = Row(read, "Timeout");
        timeout.Origin.Should().Be(ConnectionSettingOrigin.Default);
        timeout.IsExplicit.Should().BeFalse();

        read.Settings!.Rows.Should().NotContain(static x => x.Origin == ConnectionSettingOrigin.ConnectionString, "nothing of a pasted string is the data source's");
    }

    [Fact]
    public void The_pasted_settings_get_the_hints_a_data_sources_would()
    {
        PastedConnection read = Read("Host=db.example;SSL Mode=Require;Connection Lifetime=0;Host=a,b");

        read.Settings!.Hints.Select(static x => x.Id).Should().Contain(ConnectionHintIds.Tls).And.Contain(ConnectionHintIds.MultiHost);
    }

    // -------------------------------------------------------------------------------------------------
    // The reader is pure: it parses, and connects to nothing
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public void Reading_a_string_that_names_a_listening_host_opens_no_connection_to_it()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint) listener.LocalEndpoint).Port;

        PastedConnection read = Read("Host=127.0.0.1;Port=" + port + ";Database=app;Username=svc;Password=" + Sentinel + ";Timeout=1;Pooling=true;Minimum Pool Size=5");

        read.Outcome.Should().Be(PastedConnectionOutcome.Read);
        listener.Pending().Should().BeFalse("the string is parsed and never connected to");
    }

    [Fact]
    public void The_sanitiser_and_the_comparer_have_no_way_to_open_a_connection_or_a_file()
    {
        string root = RepositoryRootPath();
        string[] files =
        [
            Path.Combine(root, "src", "PostgresStudio.Diagnostics", "Services", "Connection", "PastedConnectionString.cs"),
            Path.Combine(root, "src", "PostgresStudio", "Services", "Connection", "ConnectionComparer.cs"),
            Path.Combine(root, "src", "PostgresStudio", "Services", "Connection", "ConnectionCompareService.cs"),
            Path.Combine(root, "src", "PostgresStudio", "Components", "Pages", "Database", "ConnectionCompare.razor"),
        ];

        foreach (string file in files)
        {
            // The documentation says what the class never does; the code is what is held to it.
            string code = string.Join('\n', File.ReadAllLines(file).Where(static x => !x.TrimStart().StartsWith("//", StringComparison.Ordinal)));

            foreach (string forbidden in new[] { "new NpgsqlConnection(", "NpgsqlDataSource", "OpenAsync", "OpenConnection", "TcpClient", "Socket", "File.", "ILogger", "StudioActionLog", "audit." })
            {
                code.Should().NotContain(forbidden, Path.GetFileName(file) + " must never reach for " + forbidden);
            }
        }
    }

    private static string RepositoryRootPath()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "marten-studio.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found above " + AppContext.BaseDirectory);
    }
}
