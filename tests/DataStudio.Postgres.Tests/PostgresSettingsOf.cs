using DataStudio.Postgres.Services.Postgres;
using DataStudio.Services.Settings;

using Microsoft.Extensions.Options;

namespace DataStudio.Postgres.Tests;

/// <summary>
/// The Postgres module's settings built by hand, for a test that has no container: the module's options over a default shell
/// (<c>/studio</c>, nothing read-only, no entry policy). C# 14 static extension members, so the tests keep calling
/// <c>PostgresSettings.Of(options)</c>.
/// </summary>
/// <remarks>
/// <b>Test-only on purpose.</b> Production code never builds a default shell: it takes the registered settings (the container's
/// <c>IStudioSettings</c> or <c>IShellSettings</c>), so a host's own <c>DataStudioOptions</c> is the one source of truth.
/// </remarks>
internal static class PostgresSettingsOf
{
    extension(PostgresSettings)
    {
        /// <summary>The settings over <paramref name="options" /> and <paramref name="shell" />.</summary>
        public static PostgresSettings Of(PostgresOptions options, IShellSettings shell)
        {
            ArgumentNullException.ThrowIfNull(options);
            return new PostgresSettings(Options.Create(options), shell);
        }

        /// <summary>The settings over <paramref name="options" /> and a default shell.</summary>
        public static PostgresSettings Of(PostgresOptions options) => PostgresSettings.Of(options, DefaultShell());

        /// <summary>The settings over <paramref name="options" /> and a default shell.</summary>
        public static PostgresSettings Of(IOptions<PostgresOptions> options) => new(options, DefaultShell());
    }

    private static ShellSettings DefaultShell() => new(Options.Create(new DataStudioOptions()));
}
