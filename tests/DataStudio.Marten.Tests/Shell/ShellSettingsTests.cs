using DataStudio.Services.Settings;
using DataStudio.Marten.Tests.Components;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using DataStudio.Postgres.Services;
using DataStudio.Postgres.Services.Settings;

namespace DataStudio.Marten.Tests.Shell;

/// <summary>v2 step 4: <see cref="IShellSettings" /> is the shell's own object; the Marten module's settings answer the shell's members from it.</summary>
public sealed class ShellSettingsTests
{
    [Fact]
    public void The_shell_settings_are_the_shells_own_and_the_modules_spell_their_master_switch_as_a_marten_host_does()
    {
        using StudioComponentContext context = new();

        IShellSettings shell = context.Services.GetRequiredService<IShellSettings>();
        IStudioSettings module = context.Services.GetRequiredService<IStudioSettings>();

        shell.Should().NotBeSameAs(module, "the DI service IShellSettings is the shell's object, never a module's");
        shell.ReadOnlyOptionName.Should().Be("DataStudioOptions.ReadOnly");
        shell.ProductName.Should().Be("Data Studio");
        module.ProductName.Should().Be("Data Studio");
        module.ReadOnlyOption().Should().Be("MartenOptions.ReadOnly", "with neither switch on, the module's is the one a host reads to learn which exists");
    }

    /// <summary>The two objects hold on a container the real <c>AddDataStudio().AddMarten()</c> builds, not only on a test context.</summary>
    [Fact]
    public void AddMarten_leaves_the_shells_settings_the_shells_and_adds_the_modules()
    {
        ServiceCollection services = new();
        services.AddDataStudio().AddMarten();
        using ServiceProvider provider = services.BuildServiceProvider();

        provider.GetRequiredService<IShellSettings>().Should().BeOfType<ShellSettings>();
        provider.GetRequiredService<IStudioSettings>().Should().BeOfType<DataStudio.Marten.Services.MartenStudioSettings>();
        provider.GetRequiredService<IStudioSettings>().Should().NotBeSameAs(provider.GetRequiredService<IShellSettings>());
    }

    /// <summary>
    /// Decision 12: the shell's read-only switch turns every capability of the module off and names <c>DataStudioOptions.ReadOnly</c>; the
    /// module's own turns only its own off and names <c>MartenOptions.ReadOnly</c>.
    /// </summary>
    [Fact]
    public void The_shells_read_only_names_the_shells_option_and_the_modules_names_its_own()
    {
        ServiceCollection services = new();
        services.AddDataStudio().AddMarten(static options => options.Capabilities = MartenCapabilities.All());
        using ServiceProvider provider = services.BuildServiceProvider();
        IStudioSettings settings = provider.GetRequiredService<IStudioSettings>();
        StudioCapabilityGuard guard = new(settings);

        guard.IsEnabled(StudioCapability.EditDocuments).Should().BeTrue();

        provider.GetRequiredService<IOptions<MartenOptions>>().Value.ReadOnly = true;
        StudioCapabilityGuard.All.Should().OnlyContain(capability => !guard.IsEnabled(capability), "the module's switch turns every capability of the module off");
        FluentActions.Invoking(() => guard.Require(StudioCapability.EditDocuments)).Should().Throw<StudioCapabilityDeniedException>().WithMessage("*MartenOptions.ReadOnly*");

        provider.GetRequiredService<IOptions<DataStudioOptions>>().Value.ReadOnly = true;
        FluentActions.Invoking(() => guard.Require(StudioCapability.EditDocuments)).Should().Throw<StudioCapabilityDeniedException>().WithMessage("*DataStudioOptions.ReadOnly*");

        provider.GetRequiredService<IOptions<MartenOptions>>().Value.ReadOnly = false;
        StudioCapabilityGuard.All.Should().OnlyContain(capability => !guard.IsEnabled(capability), "the shell's switch alone turns the module's capabilities off");

        provider.GetRequiredService<IOptions<DataStudioOptions>>().Value.ReadOnly = false;
        guard.IsEnabled(StudioCapability.EditDocuments).Should().BeTrue();
    }
}
