using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DataStudio.Tests.Registration;

/// <summary>
/// The shell's own options (v2 step 4): the defaults, and the validation that moved here from the two database modules' options - the mount
/// path, the application path and the poll interval - in the shell's names.
/// </summary>
public class DataStudioOptionsTests
{
    private static DataStudioOptions Resolve(Action<DataStudioOptions> configure)
    {
        ServiceCollection services = new();
        services.AddDataStudio(configure);
        using ServiceProvider provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<DataStudioOptions>>().Value;
    }

    private static Action ResolveAction(Action<DataStudioOptions> configure) => () => Resolve(configure);

    [Fact]
    public void The_defaults_are_the_shells()
    {
        DataStudioOptions options = new();

        options.Path.Should().Be("/studio");
        options.Title.Should().Be("Data Studio");
        options.ApplicationPath.Should().BeNull();
        options.AuthorizationPolicy.Should().BeNull();
        options.ReadOnly.Should().BeFalse();
        options.RefreshInterval.Should().Be(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void A_valid_configuration_resolves()
    {
        DataStudioOptions options = Resolve(static options =>
        {
            options.Path = "/ops/studio";
            options.Title = "Ops";
            options.AuthorizationPolicy = "ops";
            options.ReadOnly = true;
            options.RefreshInterval = TimeSpan.FromSeconds(30);
        });

        options.Path.Should().Be("/ops/studio");
        options.Title.Should().Be("Ops");
        options.AuthorizationPolicy.Should().Be("ops");
        options.ReadOnly.Should().BeTrue();
        options.RefreshInterval.Should().Be(TimeSpan.FromSeconds(30));
    }

    [Theory]
    [InlineData("studio")]
    [InlineData("/a/{id}")]
    [InlineData("/a/../b")]
    public void An_invalid_path_fails(string path)
    {
        ResolveAction(options => options.Path = path).Should().Throw<OptionsValidationException>().WithMessage("DataStudioOptions.Path*");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(301)]
    public void The_refresh_interval_is_bounded(int seconds)
    {
        ResolveAction(options => options.RefreshInterval = TimeSpan.FromSeconds(seconds)).Should().Throw<OptionsValidationException>()
            .WithMessage("*DataStudioOptions.RefreshInterval must be between one second and five minutes*");
    }

    /// <summary>1.2-HEADER: no link unless the host sets one.</summary>
    [Fact]
    public void ApplicationPath_is_null_by_default()
    {
        Resolve(static _ => { }).ApplicationPath.Should().BeNull();
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/app")]
    [InlineData("https://example.com/app")]
    public void An_application_path_that_is_rooted_or_an_absolute_http_url_is_accepted(string path)
    {
        Resolve(options => options.ApplicationPath = path).ApplicationPath.Should().Be(path);
    }

    [Theory]
    [InlineData("")]
    [InlineData("app")]
    [InlineData("//evil.example/app")]
    [InlineData("/\\evil.example")]
    [InlineData("/app with space")]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://user:pass@evil.com/")]
    [InlineData("https://app.example.com@evil.com/")]
    [InlineData("JAVASCRIPT:alert(1)")]
    [InlineData("data:text/html,x")]
    [InlineData(" javascript:x")]
    [InlineData("\\\\evil.com\\x")]
    public void An_application_path_that_is_neither_is_refused_at_startup(string path)
    {
        ResolveAction(options => options.ApplicationPath = path).Should().Throw<OptionsValidationException>()
            .WithMessage("*DataStudioOptions.ApplicationPath must be null*absolute http or https URL*");
    }

    /// <summary>1.2-FINAL-FIX: a host with <c>https://user@host/</c> is told why it failed, not just which shapes are allowed.</summary>
    [Theory]
    [InlineData("https://user@host.example/")]
    [InlineData("https://user:pass@host.example/")]
    public void An_application_URL_with_user_information_is_refused_and_the_message_says_so(string path)
    {
        ResolveAction(options => options.ApplicationPath = path).Should().Throw<OptionsValidationException>()
            .WithMessage("*DataStudioOptions.ApplicationPath must be null*no user information*'https://user@host/'*");
    }

    [Fact]
    public void Calling_AddDataStudio_twice_configures_the_options_twice_and_reports_a_failure_once()
    {
        ServiceCollection services = new();
        services.AddDataStudio(static options => options.Title = "first");
        services.AddDataStudio(static options => options.AuthorizationPolicy = "second");
        services.AddDataStudio(static options => options.Path = "studio");
        using ServiceProvider provider = services.BuildServiceProvider();

        Action read = () => _ = provider.GetRequiredService<IOptions<DataStudioOptions>>().Value;

        OptionsValidationException refused = read.Should().Throw<OptionsValidationException>().Which;
        refused.Failures.Count(static x => x.StartsWith("DataStudioOptions.Path must start with", StringComparison.Ordinal)).Should().Be(1);
    }

    // -------------------------------------------------------------------------------------------
    // The derived-path cache
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// <c>MapDataStudio(pattern)</c> writes the pattern back onto the resolved options instance, so the three derived values have to follow it
    /// rather than being frozen at first read.
    /// </summary>
    [Fact]
    public void The_derived_path_cache_follows_a_path_that_changes()
    {
        DataStudioOptions options = new();

        options.TrimmedPath.Should().Be("/studio");
        options.EscapedPath.Should().Be("/studio");
        options.HasCustomPath.Should().BeFalse();

        options.Path = "/ops/studio";

        options.TrimmedPath.Should().Be("/ops/studio");
        options.EscapedPath.Should().Be("/ops/studio");
        options.HasCustomPath.Should().BeTrue();

        options.Path = "/studio";

        options.TrimmedPath.Should().Be("/studio");
        options.HasCustomPath.Should().BeFalse();
    }

    [Theory]
    [InlineData("/studio/", "/studio")]
    [InlineData("studio", "/studio")]
    [InlineData("  /ops/studio  ", "/ops/studio")]
    [InlineData("/", "/studio")]
    [InlineData("", "/studio")]
    public void The_trimmed_path_normalizes_what_a_host_may_have_written(string path, string expected)
    {
        new DataStudioOptions { Path = path }.TrimmedPath.Should().Be(expected);
    }

    [Fact]
    public void The_escaped_path_is_what_a_browser_puts_in_the_url()
    {
        DataStudioOptions options = new() { Path = "/ops/m\u00e4rten studio" };

        options.EscapedPath.Should().Be("/ops/m%C3%A4rten%20studio");
        options.HasCustomPath.Should().BeTrue();
    }

    [Fact]
    public void A_path_differing_only_in_casing_is_not_a_custom_path()
    {
        // Route matching is case-insensitive, so /Studio is the default path wearing a hat.
        new DataStudioOptions { Path = "/Studio" }.HasCustomPath.Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("studio")]
    public void A_path_that_is_not_rooted_is_refused(string path)
    {
        ResolveAction(options => options.Path = path).Should().Throw<OptionsValidationException>().WithMessage("*must start with '/'*");
    }

    [Theory]
    [InlineData("/tenant{env}")]
    [InlineData("/ops/../studio")]
    [InlineData("/ops/./studio")]
    [InlineData("/ops?x=1")]
    [InlineData("/ops#frag")]
    [InlineData("/ops//studio")]
    public void A_path_that_is_not_a_plain_url_path_is_refused(string path)
    {
        ResolveAction(options => options.Path = path).Should().Throw<OptionsValidationException>().WithMessage("*simple URL path*");
    }

    [Theory]
    [InlineData("/studio")]
    [InlineData("/ops/studio")]
    [InlineData("/a/b/c/d")]
    public void A_plain_rooted_path_is_accepted(string path)
    {
        Resolve(options => options.Path = path).Path.Should().Be(path);
    }
}
