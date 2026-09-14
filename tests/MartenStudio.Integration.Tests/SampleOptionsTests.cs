using MartenStudio.Sample;

using Microsoft.Extensions.Configuration;

namespace MartenStudio.Integration.Tests;

/// <summary>
/// The sample host's command line. No Docker, no Postgres and no host: this is a parser.
/// </summary>
/// <remarks>
/// <para>
/// It lives in the integration project only because that is the project that references
/// <c>MartenStudio.Sample</c> — <c>tests/MartenStudio.Tests</c> deliberately references the library and
/// nothing else, and adding a project reference for a parser test would be the wrong trade.
/// </para>
/// <para>
/// What is being pinned is the interaction between two parsers over one array. <c>SampleOptions</c> reads
/// the switches the demo understands; <c>WebApplication.CreateBuilder(args)</c> hands the same array to
/// .NET's <c>CommandLineConfigurationProvider</c>, which reads <c>--key value</c> pairs and therefore
/// swallows whatever follows a bare switch. The README used to tell people to work around that by putting
/// their pairs first. <see cref="SampleOptions.HostArguments" /> is the fix, and the last two tests here
/// drive the real configuration provider rather than asserting about it, because an assertion about
/// somebody else's parser is a guess.
/// </para>
/// </remarks>
public class SampleOptionsTests
{
    [Fact]
    public void A_bare_boolean_switch_is_true()
    {
        SampleOptions options = SampleOptions.Parse(["--anonymous", "--readonly", "--allow-data-generation"]);

        options.Anonymous.Should().BeTrue();
        options.ReadOnly.Should().BeTrue();
        options.AllowDataGeneration.Should().BeTrue();
    }

    [Fact]
    public void Nothing_at_all_is_every_switch_off()
    {
        SampleOptions options = SampleOptions.Parse([]);

        options.Anonymous.Should().BeFalse();
        options.ReadOnly.Should().BeFalse();
        options.AllowDataGeneration.Should().BeFalse();
        options.Path.Should().BeNull();
    }

    /// <summary>
    /// The bug, in one line: a bare <c>--anonymous</c> used to leave the next token ambiguous, and the
    /// host's own parser then ate <c>--urls</c> as its value.
    /// </summary>
    [Fact]
    public void A_bare_switch_does_not_consume_the_token_after_it()
    {
        SampleOptions options = SampleOptions.Parse(["--anonymous", "--urls", "http://localhost:5210", "--readonly"]);

        options.Anonymous.Should().BeTrue();
        options.ReadOnly.Should().BeTrue("the --readonly after a URL is still a switch");
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("True", true)]
    [InlineData("false", false)]
    [InlineData("FALSE", false)]
    public void A_literal_true_or_false_after_a_switch_is_its_value(string literal, bool expected)
    {
        SampleOptions options = SampleOptions.Parse(["--readonly", literal]);

        options.ReadOnly.Should().Be(expected);
    }

    [Theory]
    [InlineData("--readonly=true", true)]
    [InlineData("--readonly=false", false)]
    public void The_equals_form_is_accepted_too(string argument, bool expected)
    {
        SampleOptions.Parse([argument]).ReadOnly.Should().Be(expected);
    }

    /// <summary>
    /// Only a literal <c>true</c>/<c>false</c> is taken as a value. Anything else belongs to whoever else
    /// is reading this command line — which is the whole point.
    /// </summary>
    [Fact]
    public void A_value_that_is_not_a_boolean_is_left_where_it_was()
    {
        SampleOptions options = SampleOptions.Parse(["--anonymous", "yes"]);

        options.Anonymous.Should().BeTrue();
        SampleOptions.HostArguments(["--anonymous", "yes"]).Should().Equal("--anonymous=true", "yes");
    }

    [Fact]
    public void Path_is_a_pair_and_does_take_the_token_after_it()
    {
        SampleOptions.Parse(["--path", "/ops/marten"]).Path.Should().Be("/ops/marten");
        SampleOptions.Parse(["--path=/ops/marten"]).Path.Should().Be("/ops/marten");
    }

    [Fact]
    public void A_path_with_nothing_after_it_is_no_path_at_all()
    {
        SampleOptions.Parse(["--anonymous", "--path"]).Path.Should().BeNull();
    }

    // ------------------------------------------------------------------------------------------------
    // What the host is handed
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public void Host_arguments_rewrite_the_samples_own_switches_and_pass_everything_else_through()
    {
        string[] host = SampleOptions.HostArguments(
            ["--anonymous", "--urls", "http://localhost:5210", "--readonly", "false", "--path", "/ops/marten", "--environment", "Staging"]);

        host.Should().Equal(
            "--anonymous=true",
            "--urls",
            "http://localhost:5210",
            "--readonly=false",
            "--path=/ops/marten",
            "--environment",
            "Staging");
    }

    [Fact]
    public void Host_arguments_leave_a_command_line_with_none_of_our_switches_exactly_as_it_was()
    {
        string[] args = ["--urls", "http://localhost:5210", "--environment", "Development"];

        SampleOptions.HostArguments(args).Should().Equal(args);
    }

    /// <summary>
    /// Driven through the real provider rather than asserted about: this is the parser whose behaviour the
    /// whole rule exists to accommodate, and a hand-written model of it would be a guess that ages.
    /// </summary>
    [Fact]
    public void The_real_command_line_provider_reads_the_url_from_the_rewritten_arguments()
    {
        string[] args = ["--anonymous", "--urls", "http://localhost:5210"];

        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddCommandLine(SampleOptions.HostArguments(args))
            .Build();

        configuration["urls"].Should().Be("http://localhost:5210");
        configuration["anonymous"].Should().Be("true");
    }

    /// <summary>
    /// The same provider on the raw arguments, which is what <c>WebApplication.CreateBuilder(args)</c> was
    /// given before this change — and the reason the README carried an "argument order matters" warning.
    /// </summary>
    /// <remarks>
    /// The failure is silent, which is what makes it worth a test rather than a note.
    /// <c>--anonymous</c> takes <c>--urls</c> as its value, and the orphaned <c>http://localhost:5210</c>
    /// has no <c>-</c>, <c>--</c> or <c>/</c> prefix, so <c>Load()</c> reaches its <c>continue</c> and
    /// drops it on the floor (read off the real 10.0.12 assembly, not assumed — an older .NET threw a
    /// <c>FormatException</c> here, and the current one does not). Nothing warns, nothing throws, and
    /// Kestrel listens on the default port while the person who typed the command watches the wrong URL.
    /// </remarks>
    [Fact]
    public void The_same_provider_on_the_raw_arguments_loses_the_url_without_a_word()
    {
        string[] args = ["--anonymous", "--urls", "http://localhost:5210"];

        IConfigurationRoot configuration = new ConfigurationBuilder().AddCommandLine(args).Build();

        configuration["anonymous"].Should().Be("--urls", "the bare switch swallowed the next token as its value");
        configuration["urls"].Should().BeNull("and the URL left over is dropped silently");
    }
}
