using MartenStudio.Services;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MartenStudio.Tests;

/// <summary>
/// Options validation, the derived-path cache, and the capability truth table.
/// </summary>
/// <remarks>
/// Every rule here fails at <em>startup</em>. A page size of zero or a SQL console role with a quote in
/// it is a configuration mistake, and the only good time to find one is before the application is
/// serving.
/// </remarks>
public class MartenStudioOptionsTest
{
    private static MartenStudioOptions Resolve(Action<MartenStudioOptions> configure)
    {
        var services = new ServiceCollection();
        services.AddMartenStudio(configure);
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<MartenStudioOptions>>().Value;
    }

    private static Action ResolveAction(Action<MartenStudioOptions> configure) => () => Resolve(configure);

    [Fact]
    public void The_defaults_are_valid_and_are_the_documented_ones()
    {
        var options = Resolve(static _ => { });

        options.Path.Should().Be("/marten");
        options.Title.Should().Be("Marten Studio");
        options.AuthorizationPolicy.Should().BeNull();
        options.StoreAuthorizationPolicy.Should().BeNull();
        options.WriteAuthorizationPolicy.Should().BeNull();
        options.ReadOnly.Should().BeFalse();
        options.DefaultPageSize.Should().Be(50);
        options.MaxPageSize.Should().Be(500);
        options.QueryTimeout.Should().Be(TimeSpan.FromSeconds(30));
        options.MaxInlineDocumentBytes.Should().Be(512 * 1024);
        options.MaxSqlConsoleRows.Should().Be(500);
        options.SqlConsoleRole.Should().BeNull();
        options.ExactCountThreshold.Should().Be(100_000);
        options.RefreshInterval.Should().Be(TimeSpan.FromSeconds(5));
        options.IsDocumentTypeVisible.Should().BeNull();
        options.IncludeAncillaryStores.Should().BeTrue();
        options.KnownTenantIds.Should().BeEmpty();
        options.DiscoverTenantIds.Should().BeTrue();

        // D4: every capability is off until the host says otherwise.
        StudioCapabilityGuard.All.Should().HaveCount(9);
        foreach (var capability in StudioCapabilityGuard.All)
        {
            new StudioCapabilityGuard(Options.Create(options)).IsEnabled(capability).Should().BeFalse();
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("marten")]
    public void A_path_that_is_not_rooted_is_refused(string path)
    {
        ResolveAction(options => options.Path = path).Should().Throw<OptionsValidationException>()
            .WithMessage("*must start with '/'*");
    }

    [Theory]
    [InlineData("/tenant{env}")]
    [InlineData("/ops/../marten")]
    [InlineData("/ops/./marten")]
    [InlineData("/ops?x=1")]
    [InlineData("/ops#frag")]
    [InlineData("/ops//marten")]
    public void A_path_that_is_not_a_plain_url_path_is_refused(string path)
    {
        ResolveAction(options => options.Path = path).Should().Throw<OptionsValidationException>()
            .WithMessage("*simple URL path*");
    }

    [Theory]
    [InlineData("/marten")]
    [InlineData("/ops/marten")]
    [InlineData("/a/b/c/d")]
    public void A_plain_rooted_path_is_accepted(string path)
    {
        Resolve(options => options.Path = path).Path.Should().Be(path);
    }

    [Theory]
    [InlineData(0, 500)]
    [InlineData(-1, 500)]
    [InlineData(501, 500)]
    public void A_default_page_size_outside_one_to_MaxPageSize_is_refused(int defaultPageSize, int maxPageSize)
    {
        ResolveAction(options =>
        {
            options.DefaultPageSize = defaultPageSize;
            options.MaxPageSize = maxPageSize;
        }).Should().Throw<OptionsValidationException>().WithMessage("*DefaultPageSize*");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5001)]
    public void A_max_page_size_outside_one_to_five_thousand_is_refused(int maxPageSize)
    {
        ResolveAction(options =>
        {
            options.MaxPageSize = maxPageSize;
            options.DefaultPageSize = 1;
        }).Should().Throw<OptionsValidationException>().WithMessage("*MaxPageSize*");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(999)]
    [InlineData(600_001)]
    public void A_query_timeout_outside_one_second_to_ten_minutes_is_refused(int milliseconds)
    {
        ResolveAction(options => options.QueryTimeout = TimeSpan.FromMilliseconds(milliseconds))
            .Should().Throw<OptionsValidationException>().WithMessage("*QueryTimeout*");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData((64 * 1024 * 1024) + 1)]
    public void An_inline_document_limit_outside_one_byte_to_64_MiB_is_refused(int bytes)
    {
        ResolveAction(options => options.MaxInlineDocumentBytes = bytes)
            .Should().Throw<OptionsValidationException>().WithMessage("*MaxInlineDocumentBytes*");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10_001)]
    public void A_sql_console_row_cap_outside_one_to_ten_thousand_is_refused(int rows)
    {
        ResolveAction(options => options.MaxSqlConsoleRows = rows)
            .Should().Throw<OptionsValidationException>().WithMessage("*MaxSqlConsoleRows*");
    }

    /// <summary>
    /// The console's role is written into <c>SET LOCAL ROLE</c>, where it is an identifier and cannot be
    /// a parameter. The shape is checked here, at startup, rather than escaped later.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("1role")]
    [InlineData("role name")]
    [InlineData("role\"; drop table x; --")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void A_sql_console_role_that_is_not_a_plain_identifier_is_refused(string role)
    {
        ResolveAction(options => options.SqlConsoleRole = role)
            .Should().Throw<OptionsValidationException>().WithMessage("*SqlConsoleRole*");
    }

    [Theory]
    [InlineData("readonly_role")]
    [InlineData("_role")]
    [InlineData("Role$1")]
    public void A_plain_identifier_is_an_acceptable_sql_console_role(string role)
    {
        Resolve(options => options.SqlConsoleRole = role).SqlConsoleRole.Should().Be(role);
    }

    [Fact]
    public void A_negative_exact_count_threshold_is_refused()
    {
        ResolveAction(static options => options.ExactCountThreshold = -1)
            .Should().Throw<OptionsValidationException>().WithMessage("*ExactCountThreshold*");
    }

    [Theory]
    [InlineData(999)]
    [InlineData(300_001)]
    public void A_refresh_interval_outside_one_second_to_five_minutes_is_refused(int milliseconds)
    {
        ResolveAction(options => options.RefreshInterval = TimeSpan.FromMilliseconds(milliseconds))
            .Should().Throw<OptionsValidationException>().WithMessage("*RefreshInterval*");
    }

    [Fact]
    public void Null_capabilities_are_refused()
    {
        ResolveAction(static options => options.Capabilities = null!)
            .Should().Throw<OptionsValidationException>().WithMessage("*Capabilities*");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_tenant_id_is_refused(string tenantId)
    {
        ResolveAction(options => options.KnownTenantIds.Add(tenantId))
            .Should().Throw<OptionsValidationException>().WithMessage("*KnownTenantIds*");
    }

    // -------------------------------------------------------------------------------------------
    // The derived-path cache
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// <c>MapMartenStudio(pattern)</c> writes the pattern back onto the resolved options instance, so the
    /// three derived values have to follow it rather than being frozen at first read.
    /// </summary>
    [Fact]
    public void The_derived_path_cache_follows_a_path_that_changes()
    {
        var options = new MartenStudioOptions();

        options.TrimmedPath.Should().Be("/marten");
        options.EscapedPath.Should().Be("/marten");
        options.HasCustomPath.Should().BeFalse();

        options.Path = "/ops/marten";

        options.TrimmedPath.Should().Be("/ops/marten");
        options.EscapedPath.Should().Be("/ops/marten");
        options.HasCustomPath.Should().BeTrue();

        options.Path = "/marten";

        options.TrimmedPath.Should().Be("/marten");
        options.HasCustomPath.Should().BeFalse();
    }

    [Theory]
    [InlineData("/marten/", "/marten")]
    [InlineData("marten", "/marten")]
    [InlineData("  /ops/marten  ", "/ops/marten")]
    [InlineData("/", "/marten")]
    [InlineData("", "/marten")]
    public void The_trimmed_path_normalizes_what_a_host_may_have_written(string path, string expected)
    {
        new MartenStudioOptions { Path = path }.TrimmedPath.Should().Be(expected);
    }

    [Fact]
    public void The_escaped_path_is_what_a_browser_puts_in_the_url()
    {
        var options = new MartenStudioOptions { Path = "/ops/märten studio" };

        options.EscapedPath.Should().Be("/ops/m%C3%A4rten%20studio");
        options.HasCustomPath.Should().BeTrue();
    }

    [Fact]
    public void A_path_differing_only_in_casing_is_not_a_custom_path()
    {
        // Route matching is case-insensitive, so /Marten is the default path wearing a hat.
        new MartenStudioOptions { Path = "/Marten" }.HasCustomPath.Should().BeFalse();
    }

    // -------------------------------------------------------------------------------------------
    // Capability x ReadOnly
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void Every_capability_is_off_by_default()
    {
        var guard = new StudioCapabilityGuard(Options.Create(new MartenStudioOptions()));

        guard.EnabledCount.Should().Be(0);
        guard.ReadOnly.Should().BeFalse();
    }

    [Fact]
    public void All_turns_on_every_capability()
    {
        var guard = new StudioCapabilityGuard(Options.Create(new MartenStudioOptions
        {
            Capabilities = MartenStudioCapabilities.All()
        }));

        guard.EnabledCount.Should().Be(StudioCapabilityGuard.All.Count);
        foreach (var capability in StudioCapabilityGuard.All)
        {
            guard.IsEnabled(capability).Should().BeTrue();
        }
    }

    [Fact]
    public void ReadOnly_overrides_every_capability_however_they_were_configured()
    {
        var guard = new StudioCapabilityGuard(Options.Create(new MartenStudioOptions
        {
            Capabilities = MartenStudioCapabilities.All(),
            ReadOnly = true
        }));

        guard.EnabledCount.Should().Be(0);
        guard.ReadOnly.Should().BeTrue();

        foreach (var capability in StudioCapabilityGuard.All)
        {
            var refusal = guard.Invoking(x => x.Require(capability)).Should().Throw<StudioCapabilityDeniedException>().Which;

            refusal.Reason.Should().Be(CapabilityDenialReason.ReadOnly);
            refusal.Capability.Should().Be(capability);
            refusal.Message.Should().Contain("MartenStudioOptions.ReadOnly");
        }
    }

    /// <summary>
    /// The whole table, one capability at a time: with exactly one enabled, that one passes and the other
    /// eight refuse, each naming its own option.
    /// </summary>
    [Theory]
    [MemberData(nameof(EveryCapability))]
    public void One_enabled_capability_enables_exactly_itself(string capabilityName)
    {
        // The parameter is the capability's name rather than the enum: xunit needs a public test method,
        // and StudioCapability is internal because it is not part of the package's public surface.
        var enabled = Enum.Parse<StudioCapability>(capabilityName);

        var capabilities = new MartenStudioCapabilities();
        typeof(MartenStudioCapabilities).GetProperty(capabilityName)!.SetValue(capabilities, true);

        var guard = new StudioCapabilityGuard(Options.Create(new MartenStudioOptions { Capabilities = capabilities }));

        guard.EnabledCount.Should().Be(1);
        guard.IsEnabled(enabled).Should().BeTrue();
        guard.Invoking(x => x.Require(enabled)).Should().NotThrow();

        foreach (var other in StudioCapabilityGuard.All.Where(x => x != enabled))
        {
            guard.IsEnabled(other).Should().BeFalse();

            var refusal = guard.Invoking(x => x.Require(other)).Should().Throw<StudioCapabilityDeniedException>().Which;

            refusal.Reason.Should().Be(CapabilityDenialReason.Disabled);
            refusal.Message.Should().Contain("MartenStudioOptions.Capabilities." + other);
        }
    }

    public static TheoryData<string> EveryCapability()
    {
        var data = new TheoryData<string>();
        foreach (var capability in StudioCapabilityGuard.All)
        {
            data.Add(capability.ToString());
        }

        return data;
    }

    /// <summary>
    /// The enum and the options class must not drift: a capability added to one and forgotten in the
    /// other is a switch that cannot be set, or a property that does nothing.
    /// </summary>
    [Fact]
    public void The_capability_enum_and_the_options_class_name_the_same_operations()
    {
        var properties = typeof(MartenStudioCapabilities)
            .GetProperties()
            .Select(x => x.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        var enumNames = StudioCapabilityGuard.All.Select(x => x.ToString()).Order(StringComparer.Ordinal).ToArray();

        properties.Should().BeEquivalentTo(enumNames);
    }
}
