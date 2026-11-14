using MartenStudio.Tests.Components;

using Microsoft.Extensions.DependencyInjection;

using PostgresStudio.Services.Settings;

namespace MartenStudio.Tests.Shell;

/// <summary>Shell A1: the shell reads the studio's settings through <see cref="IShellSettings" />, which is the very same object.</summary>
public sealed class ShellSettingsTests
{
    [Fact]
    public void The_shell_settings_are_the_studios_settings_and_spell_the_master_switch_as_a_marten_host_does()
    {
        using StudioComponentContext context = new();

        IShellSettings shell = context.Services.GetRequiredService<IShellSettings>();

        shell.Should().BeSameAs(context.Services.GetRequiredService<IStudioSettings>(), "one object, two interfaces (D30)");
        shell.ReadOnlyOptionName.Should().Be("MartenStudioOptions.ReadOnly");
        shell.ProductName.Should().Be("Marten Studio");
        context.Services.GetRequiredService<IStudioSettings>().ReadOnlyOption().Should().Be("MartenStudioOptions.ReadOnly");
    }
}
