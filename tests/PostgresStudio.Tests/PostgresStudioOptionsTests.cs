using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using Npgsql;

using PostgresStudio.Services;
using PostgresStudio.Services.Postgres;
using PostgresStudio.Services.Settings;

namespace PostgresStudio.Tests;

/// <summary>
/// The options' defaults and their validation - MartenStudio's bounds and messages in PostgresStudio's own names - and the
/// source names <c>AddDataSource</c> and <c>AddDatabaseSource</c> may carry. Plus the inbound types' own checks.
/// </summary>
public class PostgresStudioOptionsTests
{
    private static PostgresStudioOptions Resolve(Action<PostgresStudioOptions> configure)
    {
        ServiceCollection services = new();
        services.AddPostgresStudio(configure);
        using ServiceProvider provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<PostgresStudioOptions>>().Value;
    }

    private static Action ResolveAction(Action<PostgresStudioOptions> configure) => () => Resolve(configure);

    [Fact]
    public void The_defaults_mirror_MartenStudios_and_name_PostgresStudio()
    {
        PostgresStudioOptions options = new();

        options.Path.Should().Be("/postgres");
        options.Title.Should().Be("Postgres Studio");
        options.AuthorizationPolicy.Should().BeNull();
        options.SourceAuthorizationPolicy.Should().BeNull();
        options.WriteAuthorizationPolicy.Should().BeNull();
        options.ReadOnly.Should().BeFalse();
        options.Capabilities.RunSql.Should().BeFalse();
        options.Capabilities.BrowseDatabase.Should().BeFalse();
        options.Capabilities.ViewDatabaseActivity.Should().BeFalse();
        options.DefaultPageSize.Should().Be(50);
        options.MaxPageSize.Should().Be(500);
        options.QueryTimeout.Should().Be(TimeSpan.FromSeconds(30));
        options.MaxInlineValueBytes.Should().Be(512 * 1024);
        options.MaxSqlConsoleRows.Should().Be(500);
        options.SqlConsoleRole.Should().BeNull();
        options.ExactCountThreshold.Should().Be(100_000);
        options.RefreshInterval.Should().Be(TimeSpan.FromSeconds(5));
        options.HealthInterval.Should().Be(TimeSpan.FromSeconds(60));
        options.BrowsableSchemas.Should().BeEmpty("the default is applied after configuration, only when nothing named a schema");
        options.IncludeDefaultDataSource.Should().BeTrue();
        options.DataSources.Should().BeEmpty();
        options.DatabaseSources.Should().BeEmpty();
    }

    [Fact]
    public void All_turns_on_the_three_reads()
    {
        PostgresStudioCapabilities all = PostgresStudioCapabilities.All();

        all.RunSql.Should().BeTrue();
        all.BrowseDatabase.Should().BeTrue();
        all.ViewDatabaseActivity.Should().BeTrue();
    }

    [Fact]
    public void A_valid_configuration_resolves()
    {
        PostgresStudioOptions options = Resolve(static options =>
        {
            options.Path = "/ops/db";
            options.BrowsableSchemas.Add("qrtz");
            options.AddDataSource("orders", static sp => sp.GetRequiredKeyedService<NpgsqlDataSource>("orders"));
            options.AddDatabaseSource("tenants.v2", static _ => throw new InvalidOperationException("never asked here"));
        });

        options.DataSources.Select(static x => x.Name).Should().Equal("orders");
        options.DatabaseSources.Select(static x => x.Name).Should().Equal("tenants.v2");
        options.BrowsableSchemas.Should().Equal(["qrtz"], "a list that names a schema replaces the default");
    }

    [Fact]
    public void BrowsableSchemas_is_public_when_nothing_names_a_schema()
    {
        Resolve(static _ => { }).BrowsableSchemas.Should().Equal("public");
    }

    /// <summary>
    /// Configuration binding appends to a list rather than replacing it, so a default written into the property would be
    /// impossible to remove from appsettings. Applied after configuration instead, and only to an empty list.
    /// </summary>
    [Fact]
    public void Bound_configuration_replaces_the_default_rather_than_adding_to_it()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PostgresStudio:BrowsableSchemas:0"] = "legacy",
                ["PostgresStudio:BrowsableSchemas:1"] = "qrtz",
            })
            .Build();

        ServiceCollection services = new();
        services.AddPostgresStudio();
        services.Configure<PostgresStudioOptions>(configuration.GetSection("PostgresStudio"));
        using ServiceProvider provider = services.BuildServiceProvider();

        provider.GetRequiredService<IOptions<PostgresStudioOptions>>().Value.BrowsableSchemas.Should().Equal("legacy", "qrtz");
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("-leading")]
    [InlineData("a/b")]
    [InlineData("a|b")]
    public void A_source_name_outside_the_key_pattern_fails(string name)
    {
        ResolveAction(options => options.AddDataSource(name, static _ => null!))
            .Should().Throw<OptionsValidationException>().WithMessage("*AddDataSource and AddDatabaseSource names must each be*");
    }

    /// <summary>
    /// A name that ends in a line break is refused by the same check and with the same sentence: .NET's <c>$</c> matches
    /// before a final line feed, so the pattern is anchored with <c>\z</c>, and "tenant_a\n" is not a key of its own.
    /// </summary>
    [Theory]
    [InlineData("tenant_a\n", false)]
    [InlineData("tenant_a\r\n", false)]
    [InlineData("tenant_a\u2028", false)]
    [InlineData("tenant_a\n", true)]
    [InlineData("tenant_a\r\n", true)]
    [InlineData("tenant_a\u2028", true)]
    public void A_source_name_ending_in_a_line_break_fails(string name, bool databaseSource)
    {
        ResolveAction(options =>
            {
                if (databaseSource)
                {
                    options.AddDatabaseSource(name, static _ => null!);
                }
                else
                {
                    options.AddDataSource(name, static _ => null!);
                }
            })
            .Should().Throw<OptionsValidationException>().WithMessage("*AddDataSource and AddDatabaseSource names must each be*");
    }

    [Fact]
    public void A_source_name_longer_than_64_characters_fails()
    {
        ResolveAction(static options => options.AddDatabaseSource(new string('a', 65), static _ => null!))
            .Should().Throw<OptionsValidationException>().WithMessage("*names must each be*");
    }

    [Theory]
    [InlineData("default")]
    [InlineData("Default")]
    public void Default_is_reserved(string name)
    {
        ResolveAction(options => options.AddDataSource(name, static _ => null!))
            .Should().Throw<OptionsValidationException>().WithMessage("*cannot name a source 'default'*");
    }

    [Fact]
    public void Names_are_unique_ignoring_case_across_both_kinds()
    {
        ResolveAction(static options => options
                .AddDataSource("orders", static _ => null!)
                .AddDatabaseSource("Orders", static _ => null!))
            .Should().Throw<OptionsValidationException>().WithMessage("*must be unique, ignoring case, across both*");
    }

    [Fact]
    public void The_add_methods_refuse_a_blank_name_and_a_null_factory_at_the_call()
    {
        PostgresStudioOptions options = new();

        ((Action) (() => options.AddDataSource(" ", static _ => null!))).Should().Throw<ArgumentException>();
        ((Action) (() => options.AddDataSource("x", null!))).Should().Throw<ArgumentNullException>();
        ((Action) (() => options.AddDatabaseSource("", static _ => null!))).Should().Throw<ArgumentException>();
        ((Action) (() => options.AddDatabaseSource("x", null!))).Should().Throw<ArgumentNullException>();
    }

    [Theory]
    [InlineData("postgres")]
    [InlineData("/a/{id}")]
    [InlineData("/a/../b")]
    public void An_invalid_path_fails(string path)
    {
        ResolveAction(options => options.Path = path).Should().Throw<OptionsValidationException>().WithMessage("PostgresStudioOptions.Path*");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(64 * 1024 * 1024 + 1)]
    public void MaxInlineValueBytes_is_bounded(int bytes)
    {
        ResolveAction(options => options.MaxInlineValueBytes = bytes)
            .Should().Throw<OptionsValidationException>().WithMessage("*PostgresStudioOptions.MaxInlineValueBytes must be between 1 and 64 MiB*");
    }

    [Fact]
    public void The_other_bounds_are_MartenStudios_in_PostgresStudios_names()
    {
        ResolveAction(static options => options.MaxPageSize = 5001).Should().Throw<OptionsValidationException>().WithMessage("*PostgresStudioOptions.MaxPageSize*");
        ResolveAction(static options => options.DefaultPageSize = 0).Should().Throw<OptionsValidationException>().WithMessage("*PostgresStudioOptions.DefaultPageSize*");
        ResolveAction(static options => options.QueryTimeout = TimeSpan.Zero).Should().Throw<OptionsValidationException>().WithMessage("*PostgresStudioOptions.QueryTimeout*");
        ResolveAction(static options => options.MaxSqlConsoleRows = 0).Should().Throw<OptionsValidationException>().WithMessage("*PostgresStudioOptions.MaxSqlConsoleRows*");
        ResolveAction(static options => options.SqlConsoleRole = "bad role").Should().Throw<OptionsValidationException>().WithMessage("*PostgresStudioOptions.SqlConsoleRole*");
        ResolveAction(static options => options.ExactCountThreshold = -1).Should().Throw<OptionsValidationException>().WithMessage("*PostgresStudioOptions.ExactCountThreshold*");
        ResolveAction(static options => options.RefreshInterval = TimeSpan.Zero).Should().Throw<OptionsValidationException>().WithMessage("*PostgresStudioOptions.RefreshInterval*");
        ResolveAction(static options => options.HealthInterval = TimeSpan.FromSeconds(1)).Should().Throw<OptionsValidationException>().WithMessage("*PostgresStudioOptions.HealthInterval*");
        ResolveAction(static options => options.Capabilities = null!).Should().Throw<OptionsValidationException>().WithMessage("*PostgresStudioCapabilities.All()*");
        ResolveAction(static options => options.BrowsableSchemas.Add("a\"b")).Should().Throw<OptionsValidationException>().WithMessage("*PostgresStudioOptions.BrowsableSchemas*");
    }

    /// <summary>The console's role is anchored the same way: a line break after a plain identifier is not one.</summary>
    [Theory]
    [InlineData("studio_reader\n")]
    [InlineData("studio_reader\r\n")]
    [InlineData("studio_reader\u2028")]
    public void A_sql_console_role_ending_in_a_line_break_fails(string role)
    {
        ResolveAction(options => options.SqlConsoleRole = role)
            .Should().Throw<OptionsValidationException>().WithMessage("*PostgresStudioOptions.SqlConsoleRole must be a plain Postgres identifier*");
        Resolve(static options => options.SqlConsoleRole = "studio_reader").SqlConsoleRole.Should().Be("studio_reader");
    }

    [Fact]
    public void The_settings_speak_PostgresStudios_names_and_offer_three_capabilities()
    {
        PostgresStudioOptions options = new() { SourceAuthorizationPolicy = "src", WriteAuthorizationPolicy = "write" };
        IStudioSettings settings = PostgresStudioSettings.Of(options);

        settings.ProductName.Should().Be("Postgres Studio");
        settings.OptionName("ReadOnly").Should().Be("PostgresStudioOptions.ReadOnly");
        settings.CapabilityOption(StudioCapability.RunSql).Should().Be("PostgresStudioOptions.Capabilities.RunSql");
        settings.AllCapabilities().Should().Be("PostgresStudioCapabilities.All()");
        settings.OptionName(settings.MaxInlineValueBytesMember).Should().Be("PostgresStudioOptions.MaxInlineValueBytes");
        settings.ScopePolicy.Should().Be("src");
        settings.WritePolicy.Should().Be("write");
        settings.Capabilities.Should().Equal(StudioCapability.RunSql, StudioCapability.BrowseDatabase, StudioCapability.ViewDatabaseActivity);
        settings.IsDocumentTypeVisible.Should().BeNull();
        settings.TrimmedPath.Should().Be("/postgres");
        settings.HasCustomPath.Should().BeFalse();
    }

    [Fact]
    public void The_source_label_is_Source_once_a_named_source_exists_and_the_descriptors_otherwise()
    {
        IStudioSettings alone = PostgresStudioSettings.Of(new PostgresStudioOptions());
        alone.SourceLabelOf(PostgresStudioDescriptor.Instance).Should().Be("Database");

        IStudioSettings withDataSource = PostgresStudioSettings.Of(new PostgresStudioOptions().AddDataSource("reporting", static _ => null!));
        withDataSource.SourceLabelOf(PostgresStudioDescriptor.Instance).Should().Be("Source");

        IStudioSettings withHost = PostgresStudioSettings.Of(new PostgresStudioOptions().AddDatabaseSource("tenants", static _ => null!));
        withHost.SourceLabelOf(PostgresStudioDescriptor.Instance).Should().Be("Source");
    }

    [Theory]
    [MemberData(nameof(EveryCapability))]
    public void Every_capability_PostgresStudio_does_not_offer_throws_rather_than_reads_false(string name)
    {
        StudioCapability capability = Enum.Parse<StudioCapability>(name);
        PostgresStudioSettings settings = PostgresStudioSettings.Of(new PostgresStudioOptions { Capabilities = PostgresStudioCapabilities.All() });

        if (settings.Capabilities.Contains(capability))
        {
            settings.IsConfigured(capability).Should().BeTrue();
        }
        else
        {
            ((Action) (() => settings.IsConfigured(capability))).Should().Throw<ArgumentOutOfRangeException>();
        }
    }

    public static TheoryData<string> EveryCapability() => [.. Enum.GetNames<StudioCapability>()];

    [Theory]
    [InlineData(-1, 0, 1, "ACU")]
    [InlineData(1, -1, 1, "ACU")]
    [InlineData(1, 2, 1, "ACU")]
    [InlineData(double.NaN, 0, 1, "ACU")]
    [InlineData(1, 0, double.PositiveInfinity, "ACU")]
    public void A_capacity_out_of_range_is_refused(double current, double minimum, double maximum, string unit)
    {
        ((Action) (() => PostgresComputeCapacity.Of(current, minimum, maximum, unit, DateTimeOffset.UnixEpoch)))
            .Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void A_capacity_needs_a_unit_and_Aurora_is_bounded_at_384_ACU()
    {
        ((Action) (() => PostgresComputeCapacity.Of(1, 0, 2, " ", DateTimeOffset.UnixEpoch))).Should().Throw<ArgumentException>();
        ((Action) (() => PostgresComputeCapacity.AuroraServerlessV2(1, 0.5, 385, DateTimeOffset.UnixEpoch))).Should().Throw<ArgumentOutOfRangeException>();
        PostgresComputeCapacity.AuroraServerlessV2(1, 0.5, 384, DateTimeOffset.UnixEpoch).Maximum.Should().Be(384,
            "platform version 4 allows 384 ACU (Performance and scaling for Aurora serverless)");

        PostgresComputeCapacity aurora = PostgresComputeCapacity.AuroraServerlessV2(8, 0.5, 16, DateTimeOffset.UnixEpoch) with { IoOptimized = true };
        aurora.Unit.Should().Be("ACU");
        aurora.Current.Should().Be(8);
        aurora.IoOptimized.Should().BeTrue();

        PostgresComputeCapacity.Of(20, 0, 16, "vCPU", DateTimeOffset.UnixEpoch).Current.Should().Be(20, "current is not checked against maximum");
    }

    [Fact]
    public void A_database_ref_needs_a_source_and_a_database_string()
    {
        ((Action) (() => _ = new PostgresDatabaseRef(" ", "acme"))).Should().Throw<ArgumentException>();
        ((Action) (() => _ = new PostgresDatabaseRef("tenants", null!))).Should().Throw<ArgumentNullException>();

        new PostgresDatabaseRef("tenants", "acme").Should().Be(new PostgresDatabaseRef("tenants", "acme"));
        new PostgresDatabaseRef("tenants", "acme").Should().NotBe(new PostgresDatabaseRef("Tenants", "acme"), "equality is ordinal");
        new PostgresDatabaseRef("default", string.Empty).Database.Should().BeEmpty();
    }

    [Fact]
    public void The_platform_kinds_have_explicit_values_that_never_move()
    {
        Enum.GetValues<PostgresPlatformKind>().Select(static x => ((int) x, x.ToString())).Should().Equal(
            (0, "SelfHosted"), (1, "AmazonRds"), (2, "AmazonAurora"), (3, "Azure"), (4, "CloudSql"), (5, "Neon"), (6, "Supabase"));
    }
}
