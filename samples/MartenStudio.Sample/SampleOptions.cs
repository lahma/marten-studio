namespace MartenStudio.Sample;

/// <summary>
/// The switches the demo host understands, read from the command line it was started with.
/// </summary>
/// <param name="Anonymous">
/// <c>--anonymous</c>: map the studio with <c>AllowAnonymous()</c> instead of a policy, to show what an
/// unauthenticated studio looks like. Never do this anywhere real.
/// </param>
/// <param name="ReadOnly">
/// <c>--readonly</c>: set <see cref="MartenStudioOptions.ReadOnly" />, which turns every mutating
/// capability off however they were configured.
/// </param>
/// <param name="Path">
/// <c>--path /ops/marten</c>: mount the studio somewhere other than <c>/marten</c>, which is what
/// exercises the sub-path re-rooting.
/// </param>
/// <param name="AllowDataGeneration">
/// <c>--allow-data-generation</c>: map the demo-data panel and its endpoints even outside Development.
/// Generating a million documents is a write, and a long one, so outside a developer's own machine it
/// has to be asked for on the command line rather than implied by being signed in as an admin.
/// </param>
internal sealed record SampleOptions(bool Anonymous, bool ReadOnly, string? Path, bool AllowDataGeneration)
{
    /// <summary>
    /// Whether the demo-data endpoints are mapped at all.
    /// </summary>
    /// <param name="environment">The host environment.</param>
    /// <remarks>
    /// Development or the explicit switch, and nothing else. A production host that never passed the
    /// switch does not have the endpoints, so the answer to a POST is 404 rather than 403 - there is
    /// nothing there to be refused.
    /// </remarks>
    public bool DataGenerationEnabled(IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        return environment.IsDevelopment() || AllowDataGeneration;
    }

    /// <summary>Reads the switches out of <paramref name="args" />, ignoring anything else.</summary>
    public static SampleOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        bool anonymous = false;
        bool readOnly = false;
        string? path = null;
        bool allowDataGeneration = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--anonymous":
                    anonymous = true;
                    break;
                case "--readonly":
                    readOnly = true;
                    break;
                case "--path" when i + 1 < args.Length:
                    path = args[++i];
                    break;
                case "--allow-data-generation":
                    allowDataGeneration = true;
                    break;
                default:
                    break;
            }
        }

        return new SampleOptions(anonymous, readOnly, path, allowDataGeneration);
    }
}
