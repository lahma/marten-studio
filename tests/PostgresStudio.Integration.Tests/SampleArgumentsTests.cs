using PostgresStudio.Sample;

namespace PostgresStudio.Integration.Tests;

/// <summary>
/// How the Postgres Studio sample reads <c>--allow-data-generation</c> (SEC-fix F2): as the Marten sample reads its switches,
/// and handed to the host builder in a form that cannot swallow the next token. No database: the parser alone.
/// </summary>
public class SampleArgumentsTests
{
    [Fact]
    public void Without_the_switch_nothing_is_allowed_and_the_command_line_passes_through_untouched()
    {
        SampleArguments parsed = SampleArguments.Parse(["--urls", "http://localhost:5211", "--environment", "Production"]);

        parsed.AllowDataGeneration.Should().BeFalse();
        parsed.HostArguments.Should().Equal("--urls", "http://localhost:5211", "--environment", "Production");
    }

    [Theory]
    [InlineData("--allow-data-generation")]
    [InlineData("--Allow-Data-Generation")]
    [InlineData("/allow-data-generation")]
    [InlineData("--allow-data-generation=true")]
    [InlineData("--allow-data-generation=TRUE")]
    public void A_bare_or_true_switch_allows_and_never_swallows_the_next_token(string token)
    {
        SampleArguments parsed = SampleArguments.Parse([token, "--urls", "http://localhost:5211"]);

        parsed.AllowDataGeneration.Should().BeTrue();
        parsed.HostArguments.Should().Equal("--allow-data-generation=true", "--urls", "http://localhost:5211");
    }

    [Fact]
    public void A_literal_value_after_the_switch_is_its_value()
    {
        SampleArguments off = SampleArguments.Parse(["--allow-data-generation", "false", "--urls", "http://localhost:5211"]);
        off.AllowDataGeneration.Should().BeFalse();
        off.HostArguments.Should().Equal("--allow-data-generation=false", "--urls", "http://localhost:5211");

        SampleArguments on = SampleArguments.Parse(["--allow-data-generation", "True"]);
        on.AllowDataGeneration.Should().BeTrue();
        on.HostArguments.Should().Equal("--allow-data-generation=true");
    }

    [Theory]
    [InlineData("--allow-data-generation=yes")]
    [InlineData("--allow-data-generation=1")]
    [InlineData("--allow-data-generation=")]
    public void Any_other_inline_value_is_refused_rather_than_read_as_false(string token)
    {
        Action parse = () => SampleArguments.Parse([token]);

        parse.Should().Throw<ArgumentException>().WithMessage("*takes true or false*");
    }
}
