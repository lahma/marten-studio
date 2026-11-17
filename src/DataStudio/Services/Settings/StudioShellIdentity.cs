namespace DataStudio.Services.Settings;

/// <summary>
/// The words the shell speaks in, which are the shell's own: its name, the lead of its anonymous banner, and the startup guard's name for what
/// it maps, its lead sentence (built from what every registered module says it exposes) and its three remedies.
/// </summary>
internal static class StudioShellIdentity
{
    /// <summary>The product's name in a sentence: the sidebar's accessible name and the rail's credit.</summary>
    public const string ProductName = "Data Studio";

    /// <summary>The startup guard's name for the endpoints the shell maps.</summary>
    public const string Surface = "Data Studio";

    /// <summary>The anonymous banner's lead, in <c>&lt;strong&gt;</c>; each module's sentence follows it in a <c>&lt;span&gt;</c> of its own.</summary>
    public const string BannerLead = "This studio is served to anyone (AllowAnonymous)";

    /// <summary>The startup guard's list of the three ways to say which authorization was meant, one per line.</summary>
    public const string Remedies = """
          - app.MapDataStudio().RequireAuthorization() authorizes its pages and its Blazor circuit;
          - services.AddDataStudio(options => options.AuthorizationPolicy = "...") authorizes those and the static assets with them;
          - app.MapDataStudio().AllowAnonymous() serves it to anyone, deliberately.
        """;

    /// <summary>
    /// The startup guard's lead sentence: that the shell is mapped with no authorization, what every registered module's pages would serve
    /// (each module's own sentence, in module order), and that the shell refuses to start.
    /// </summary>
    /// <param name="exposedSentences">Every registered module's <see cref="IStudioModule.ExposedSentence" />.</param>
    public static string Exposure(IEnumerable<string> exposedSentences)
    {
        ArgumentNullException.ThrowIfNull(exposedSentences);

        return Surface + " is mapped with no authorization. "
            + string.Join(' ', exposedSentences)
            + " " + Surface + " refuses to start rather than serve that by accident. Say which you meant:";
    }
}
