namespace MartenStudio.Tests.Conventions;

/// <summary>
/// The package ships no Blazor JS initializer, and the shell loads its script itself. Issue #2.
/// </summary>
/// <remarks>
/// <para>
/// A file named <c>*.lib.module.js</c> in an RCL's <c>wwwroot</c> is a JS initializer by convention -
/// nothing declares it, the filename alone is the declaration - and Blazor loads the initializers of
/// every referenced RCL into <em>every</em> Blazor app in the process. So a host that runs a Blazor
/// Server app of its own fetched the studio's initializer from the site root on every page it served,
/// including pages that never mention the studio; where <see cref="MartenStudioOptions.AuthorizationPolicy" />
/// is set that path is authorized too, so the fetch redirected to the login page, came back as
/// <c>text/html</c>, and the browser logged a MIME-type error on every page load - the host's own
/// unauthenticated login page included.
/// </para>
/// <para>
/// That is the host's behaviour changed by the act of referencing this package, which is the one thing
/// this project refuses. The remedy is the filename, so the guard is the filename: nothing under
/// <c>wwwroot</c> may match the convention again, and the shell has to keep asking for the script it
/// replaced it with. Neither half is visible in any rendered markup, and the failure is silent - the
/// studio works perfectly while the host's console fills up.
/// </para>
/// </remarks>
public class NoJsInitializerTests
{
    private static readonly string WebRoot = RepositoryRoot.Combine("src", "MartenStudio", "wwwroot");

    /// <summary>The script the shell loads instead, as an asset path and as a file.</summary>
    private const string HelperScript = "_content/MartenStudio/js/marten-studio.js";

    [Fact]
    public void No_static_web_asset_is_named_like_a_Blazor_JS_initializer()
    {
        Directory.Exists(WebRoot).Should().BeTrue("a vacuous pass here would let the initializer back in");

        string[] initializers = Directory
            .GetFiles(WebRoot, "*.lib.module.js", SearchOption.AllDirectories)
            .Select(x => Path.GetRelativePath(WebRoot, x).Replace('\\', '/'))
            .ToArray();

        initializers.Should().BeEmpty(
            "Blazor loads an RCL's JS initializers into every Blazor app in the host process, so a file " +
            "named this way runs in applications that never open the studio; found: " +
            string.Join(", ", initializers));
    }

    /// <summary>
    /// The other half: the helpers are only useful if something asks for them, and only the studio may.
    /// </summary>
    [Fact]
    public void The_shell_loads_the_helpers_itself_from_a_studio_rooted_path()
    {
        File.Exists(Path.Combine(WebRoot, "js", "marten-studio.js")).Should().BeTrue();

        string shell = File.ReadAllText(
            RepositoryRoot.Combine("src", "MartenStudio", "Components", "MartenStudioApp.razor"));

        shell.Should().Contain($"<script src=\"{HelperScript}\">",
            "the shell is what loads the helpers now that no convention does it");

        // Relative, so it resolves against the studio-rooted <base href> and lands on the mirror the
        // studio maps under its own path. A leading slash would send it to the application root, which is
        // exactly where a sub-path mount has nothing.
        shell.Should().NotContain($"\"/{HelperScript}\"", "an absolute path ignores the mount path");

        shell.IndexOf(HelperScript, StringComparison.Ordinal)
            .Should().BeLessThan(shell.IndexOf("_framework/blazor.web.js", StringComparison.Ordinal),
                "blazor.web.js starts the circuit that calls these helpers, so they are defined first");
    }
}
