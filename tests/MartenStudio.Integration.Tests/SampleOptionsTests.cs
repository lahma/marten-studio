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
    // What is refused rather than guessed at
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// An <c>=</c> value that is not a literal <c>true</c> or <c>false</c> is a mistake, not somebody
    /// else's token.
    /// </summary>
    /// <remarks>
    /// Writing the <c>=</c> is a claim that this token is the whole argument, so there is nobody else it
    /// could belong to — and what the parser did with it was yield <see langword="false" />. On
    /// <c>--readonly</c>, the one switch whose whole job is to turn writes <em>off</em>,
    /// <c>--readonly=yes</c> therefore started a studio that could write. A parser that fails open on
    /// that switch is worse than one that does not support it.
    /// </remarks>
    [Theory]
    [InlineData("--readonly=yes")]
    [InlineData("--readonly=1")]
    [InlineData("--readonly=on")]
    [InlineData("--anonymous=no")]
    public void An_equals_value_that_is_not_a_boolean_is_refused_rather_than_read_as_false(string argument)
    {
        Action parse = () => SampleOptions.Parse([argument]);

        parse.Should().Throw<ArgumentException>()
            .WithMessage("*true or false*", "the message has to say what would have worked");
    }

    /// <summary>
    /// Both spellings .NET's own command-line provider accepts are switches here too.
    /// </summary>
    /// <remarks>
    /// <c>--Anonymous</c> and <c>/anonymous</c> fell through to <c>default</c>, were passed to the host
    /// verbatim, and the provider then swallowed the token after them exactly as it does for any bare
    /// <c>--key</c> — which is the whole failure <see cref="SampleOptions.HostArguments" /> exists to
    /// stop. A switch that silently means nothing because of how it was capitalised is worse than one
    /// that is not supported at all.
    /// </remarks>
    [Theory]
    [InlineData("--Anonymous")]
    [InlineData("--ANONYMOUS")]
    [InlineData("/anonymous")]
    [InlineData("/Anonymous")]
    public void A_switch_is_recognised_whatever_its_case_and_however_it_is_prefixed(string spelling)
    {
        string[] args = [spelling, "--urls", "http://localhost:5210"];

        SampleOptions.Parse(args).Anonymous.Should().BeTrue();

        SampleOptions.HostArguments(args).Should().Equal(
            "--anonymous=true",
            "--urls",
            "http://localhost:5210");

        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddCommandLine(SampleOptions.HostArguments(args))
            .Build();

        configuration["urls"].Should().Be("http://localhost:5210", "the URL survives the rewrite");
    }

    /// <summary>A token that merely starts with <c>/</c> is not a switch and is passed through as it was.</summary>
    [Fact]
    public void A_path_shaped_token_that_is_not_one_of_our_switches_is_left_alone()
    {
        string[] args = ["/c/Users/somebody/app", "--urls", "http://localhost:5210"];

        SampleOptions.HostArguments(args).Should().Equal(args);
    }

    /// <summary>
    /// <c>--path</c> takes the token after it, and another switch is not a mount path.
    /// </summary>
    /// <remarks>
    /// <c>--path --urls http://localhost:5210</c> mounted the studio at <c>"--urls"</c> — which
    /// <c>MartenStudioOptions</c> would then have refused with a message about a path, several screens
    /// later — and consumed <c>--urls</c> on the way, so Kestrel listened on the default port too.
    /// </remarks>
    [Theory]
    [InlineData("--path --urls http://localhost:5210")]
    [InlineData("--path -p")]
    [InlineData("--path=--urls")]
    public void A_path_value_that_is_another_switch_is_refused(string commandLine)
    {
        Action parse = () => SampleOptions.Parse(commandLine.Split(' '));

        parse.Should().Throw<ArgumentException>().WithMessage("*no mount path*");
    }

    /// <summary>And a real path is still taken, in every spelling, so the guard is a guard.</summary>
    [Theory]
    [InlineData("--path /ops/marten")]
    [InlineData("--path=/ops/marten")]
    [InlineData("/PATH /ops/marten")]
    [InlineData("--Path=/ops/marten")]
    public void A_path_that_is_a_path_is_still_taken(string commandLine)
    {
        SampleOptions.Parse(commandLine.Split(' ')).Path.Should().Be("/ops/marten");
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
