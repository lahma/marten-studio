using DataStudio.Marten.Services;
using DataStudio.Services.Settings;

using Microsoft.Extensions.Options;

namespace DataStudio.Marten.Tests;

/// <summary>
/// The Marten module's settings built by hand, for a test that has no container: the module's options over a default shell
/// (<c>/studio</c>, nothing read-only, no entry policy). C# 14 static extension members, so the tests keep calling
/// <c>MartenSettings.Of(options)</c>.
/// </summary>
/// <remarks>
/// <b>Test-only on purpose.</b> Production code never builds a default shell: it takes the registered settings (the container's
/// <c>IStudioSettings</c> or <c>IShellSettings</c>), so a host's own <c>DataStudioOptions</c> is the one source of truth.
/// </remarks>
internal static class MartenSettingsOf
{
    extension(MartenSettings)
    {
        /// <summary>The settings over <paramref name="options" /> and <paramref name="shell" />.</summary>
        public static MartenSettings Of(MartenOptions options, IShellSettings shell)
        {
            ArgumentNullException.ThrowIfNull(options);
            return new MartenSettings(Options.Create(options), shell);
        }

        /// <summary>The settings over <paramref name="options" /> and a default shell.</summary>
        public static MartenSettings Of(MartenOptions options) => MartenSettings.Of(options, DefaultShell());

        /// <summary>The settings over <paramref name="options" /> and a default shell.</summary>
        public static MartenSettings Of(IOptions<MartenOptions> options) => new(options, DefaultShell());
    }

    private static ShellSettings DefaultShell() => new(Options.Create(new DataStudioOptions()));
}
